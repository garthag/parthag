"""Tests the client against an in-process fake of the Supabase REST/Auth API."""
import json
import os
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

from construct_dpr import ConstructDPR, ConstructDPRError
from construct_dpr import cli

EVENTS = [{"id": i, "project_id": "p1", "event_type": "dpr.approved" if i % 2 else "dpr.submitted", "entity_id": f"d{i}"} for i in range(1, 1201)]


class Fake(BaseHTTPRequestHandler):
    log = []
    state = {"token": "t1", "fail_503": 0, "expire_next": False, "upserts": []}

    def log_message(self, *a):  # silence
        pass

    def _send(self, code, body):
        raw = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _handle(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or "null")
        Fake.log.append((self.command, u.path, q, dict(self.headers), body))
        st = Fake.state
        if self.headers.get("apikey") != "anon":
            return self._send(401, {"message": "No API key"})
        if u.path == "/auth/v1/token":
            if q["grant_type"] == "password":
                if body["password"] != "secret":
                    return self._send(400, {"error_description": "Invalid login credentials"})
                return self._send(200, {"access_token": "t1", "refresh_token": "r1", "expires_in": 3600, "user": {"id": "u1"}})
            st["token"] = "t2"
            return self._send(200, {"access_token": "t2", "refresh_token": "r2", "expires_in": 3600})
        if self.headers.get("Authorization") != f"Bearer {st['token']}" or st["expire_next"]:
            st["expire_next"] = False
            st["token"] = "t-refresh-required"
            return self._send(401, {"message": "JWT expired", "code": "PGRST301"})
        if st["fail_503"]:
            st["fail_503"] -= 1
            return self._send(503, {"message": "busy"})
        if u.path == "/rest/v1/projects":
            rows = [{"id": "p1", "code": "BF2", "name": "Blast Furnace"}]
            return self._send(200, [r for r in rows if q.get("code") in (None, "eq.BF2")])
        if u.path == "/rest/v1/integration_events":
            after = int(q["id"].split(".")[1])
            lim = int(q["limit"])
            return self._send(200, [e for e in EVENTS if e["id"] > after][:lim])
        if u.path == "/rest/v1/activities" and self.command == "POST":
            st["upserts"].append((q, self.headers.get("Prefer"), body))
            return self._send(201, body)
        if u.path == "/rest/v1/rpc/submit_dpr":
            return self._send(409, {"message": "CONFLICT: DPR changed", "code": "PT409"})
        if u.path == "/rest/v1/rpc/export_project":
            return self._send(200, {"format": "construct-dpr-export/1", "dpr_headers": [1, 2], "activities": [1]})
        if u.path == "/rest/v1/activity_progress":
            return self._send(200, [{"code": "A1", "name": "Erect", "progress_pct": 12.5, "meta": {"x": 1}}])
        return self._send(404, {"message": "not found"})

    do_GET = do_POST = _handle


class ClientTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = HTTPServer(("127.0.0.1", 0), Fake)
        cls.url = f"http://127.0.0.1:{cls.srv.server_port}"
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()

    def setUp(self):
        Fake.log.clear()
        Fake.state.update(token="t1", fail_503=0, expire_next=False, upserts=[])
        self.api = ConstructDPR(self.url, "anon", retries=2)
        self.api.sign_in("bot@example.com", "secret")

    def test_sign_in_and_select_send_key_token_and_filters(self):
        self.assertEqual(self.api.project_by_code("BF2")["id"], "p1")
        method, path, q, headers, _ = Fake.log[-1]
        self.assertEqual((method, path), ("GET", "/rest/v1/projects"))
        self.assertEqual(q, {"select": "*", "code": "eq.BF2"})
        self.assertEqual(headers["Authorization"], "Bearer t1")

    def test_bad_password_raises_readable_error(self):
        with self.assertRaises(ConstructDPRError) as c:
            ConstructDPR(self.url, "anon").sign_in("bot@example.com", "wrong")
        self.assertIn("Invalid login credentials", str(c.exception))

    def test_unknown_project_is_404(self):
        with self.assertRaises(ConstructDPRError) as c:
            self.api.project_by_code("NOPE")
        self.assertEqual(c.exception.status, 404)

    def test_expired_token_is_refreshed_transparently(self):
        Fake.state["expire_next"] = True
        self.assertEqual(len(self.api.projects()), 1)
        self.assertEqual(self.api.access_token, "t2")

    def test_transient_503_is_retried(self):
        Fake.state["fail_503"] = 2
        self.assertEqual(len(self.api.projects()), 1)

    def test_conflict_error_is_flagged(self):
        with self.assertRaises(ConstructDPRError) as c:
            self.api.rpc("submit_dpr", p_id="d1")
        self.assertTrue(c.exception.is_conflict)
        self.assertEqual(c.exception.code, "PT409")

    def test_upsert_activities_merges_on_project_and_code(self):
        self.api.upsert_activities("p1", [{"code": "ERP-1", "name": "From ERP", "total_qty": 10}])
        q, prefer, body = Fake.state["upserts"][0]
        self.assertEqual(q["on_conflict"], "project_id,code")
        self.assertIn("merge-duplicates", prefer)
        self.assertEqual(body, [{"code": "ERP-1", "name": "From ERP", "total_qty": 10, "project_id": "p1"}])

    def test_events_are_paged_in_order_without_gaps(self):
        ids = [e["id"] for e in self.api.follow_events(0, stop_when_idle=True)]
        self.assertEqual(ids, list(range(1, 1201)))
        resumed = [e["id"] for e in self.api.follow_events(1150, stop_when_idle=True)]
        self.assertEqual(resumed, list(range(1151, 1201)))

    def test_cli_export_and_progress_csv(self):
        env = {"CDPR_URL": self.url, "CDPR_ANON_KEY": "anon", "CDPR_EMAIL": "bot@example.com", "CDPR_PASSWORD": "secret"}
        old = dict(os.environ)
        os.environ.update(env)
        try:
            with tempfile.TemporaryDirectory() as d:
                out = os.path.join(d, "x.json")
                self.assertEqual(cli.main(["export", "BF2", "-o", out]), 0)
                self.assertEqual(json.load(open(out))["format"], "construct-dpr-export/1")
                csv_out = os.path.join(d, "p.csv")
                self.assertEqual(cli.main(["progress", "BF2", "-o", csv_out]), 0)
                text = open(csv_out, encoding="utf-8-sig").read()
                self.assertIn("A1,Erect,12.5", text)
                self.assertIn('"{""x"": 1}"', text)
        finally:
            os.environ.clear()
            os.environ.update(old)


if __name__ == "__main__":
    unittest.main()
