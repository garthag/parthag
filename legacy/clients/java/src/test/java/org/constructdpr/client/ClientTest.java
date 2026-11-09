package org.constructdpr.client;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

/** Dependency-free tests against an in-process fake of the Supabase API. Run: ./build.sh test */
public class ClientTest {
    static final List<String> log = new ArrayList<>();
    static volatile String token = "t1";
    static final AtomicInteger fail503 = new AtomicInteger();
    static volatile boolean expireNext;
    static int passed;

    public static void main(String[] args) throws Exception {
        HttpServer srv = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        srv.createContext("/", ClientTest::handle);
        srv.start();
        String url = "http://127.0.0.1:" + srv.getAddress().getPort();
        try {
            var api = new ConstructDprClient(url, "anon", 2);
            api.signIn("bot@example.com", "secret");
            check("sign-in stores token", "t1".equals(api.getAccessToken()));

            String p = api.select("projects", Map.of("code", "eq.BF2"));
            check("select returns JSON", p.contains("\"code\":\"BF2\""));
            check("select sends filters and auth", last().contains("GET /rest/v1/projects?select=*&code=eq.BF2") && last().contains("Bearer t1") && last().contains("apikey=anon"));

            expireNext = true;
            api.projects();
            check("expired JWT refreshed transparently", "t2".equals(api.getAccessToken()));

            fail503.set(2);
            api.projects();
            check("503 retried", true);

            api.upsert("activities", "[{\"code\":\"ERP-1\",\"name\":\"From \\\"ERP\\\"\"}]", "project_id,code");
            check("upsert uses on_conflict and merge header", last().contains("on_conflict=project_id,code") && last().contains("merge-duplicates"));

            try {
                api.rpc("submit_dpr", Json.object(Map.of("p_id", "d1")));
                check("conflict raises", false);
            } catch (ConstructDprException e) {
                check("conflict raises with code", e.isConflict() && "PT409".equals(e.getCode()));
            }

            check("events query uses cursor", api.events(42, 500).startsWith("[") && last().contains("id=gt.42") && last().contains("order=id"));

            try {
                new ConstructDprClient(url, "anon", 0).signIn("bot@example.com", "wrong");
                check("bad login raises", false);
            } catch (ConstructDprException e) {
                check("bad login message", e.getMessage().contains("Invalid login credentials"));
            }

            check("Json.quote escapes", Json.quote("a\"b\\c\nd").equals("\"a\\\"b\\\\c\\nd\""));
            check("Json.object", Json.object(new java.util.LinkedHashMap<>(Map.of("n", 1))).equals("{\"n\":1}"));
        } finally {
            srv.stop(0);
        }
        System.out.println("ALL " + passed + " JAVA CLIENT TESTS PASSED");
    }

    static String last() { return log.get(log.size() - 1); }

    static void check(String name, boolean ok) {
        if (!ok) throw new AssertionError("FAILED: " + name + " (last request: " + (log.isEmpty() ? "-" : last()) + ")");
        passed++;
        System.out.println("ok - " + name);
    }

    static void handle(HttpExchange ex) throws IOException {
        String body = new String(ex.getRequestBody().readAllBytes(), StandardCharsets.UTF_8);
        String path = ex.getRequestURI().getRawPath() + (ex.getRequestURI().getRawQuery() == null ? "" : "?" + java.net.URLDecoder.decode(ex.getRequestURI().getRawQuery(), StandardCharsets.UTF_8));
        String auth = ex.getRequestHeaders().getFirst("Authorization");
        log.add(ex.getRequestMethod() + " " + path + " auth=" + auth + " apikey=" + ex.getRequestHeaders().getFirst("apikey") + " prefer=" + ex.getRequestHeaders().getFirst("Prefer"));
        if (path.startsWith("/auth/v1/token?grant_type=password")) {
            if (!body.contains("\"secret\"")) { reply(ex, 400, "{\"error_description\":\"Invalid login credentials\"}"); return; }
            reply(ex, 200, "{\"access_token\":\"t1\",\"refresh_token\":\"r1\",\"expires_in\":3600}"); return;
        }
        if (path.startsWith("/auth/v1/token?grant_type=refresh_token")) { token = "t2"; reply(ex, 200, "{\"access_token\":\"t2\",\"refresh_token\":\"r2\",\"expires_in\":3600}"); return; }
        if (expireNext || !("Bearer " + token).equals(auth)) { expireNext = false; token = "need-refresh"; reply(ex, 401, "{\"message\":\"JWT expired\"}"); return; }
        if (fail503.get() > 0) { fail503.decrementAndGet(); reply(ex, 503, "{\"message\":\"busy\"}"); return; }
        if (path.startsWith("/rest/v1/projects")) { reply(ex, 200, "[{\"id\":\"p1\",\"code\":\"BF2\"}]"); return; }
        if (path.startsWith("/rest/v1/activities")) { reply(ex, 201, body); return; }
        if (path.startsWith("/rest/v1/integration_events")) { reply(ex, 200, "[{\"id\":43,\"event_type\":\"dpr.approved\"}]"); return; }
        if (path.startsWith("/rest/v1/rpc/submit_dpr")) { reply(ex, 409, "{\"message\":\"CONFLICT: changed\",\"code\":\"PT409\"}"); return; }
        reply(ex, 404, "{\"message\":\"not found\"}");
    }

    static void reply(HttpExchange ex, int status, String json) throws IOException {
        byte[] b = json.getBytes(StandardCharsets.UTF_8);
        ex.getResponseHeaders().add("Content-Type", "application/json");
        ex.sendResponseHeaders(status, b.length);
        ex.getResponseBody().write(b);
        ex.close();
    }
}
