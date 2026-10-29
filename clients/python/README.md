# construct-dpr (Python client)

Standard-library-only client and CLI for the CONSTRUCT DPR API. Python 3.9+.

```python
from construct_dpr import ConstructDPR
api = ConstructDPR("https://xyz.supabase.co", "public-anon-key")
api.sign_in("erp-bot@company.com", "…")          # account with the "integration" project role
project = api.project_by_code("DEMO-BF2")
progress = api.activity_progress(project["id"])
for event in api.follow_events(after=0):          # dpr.approved, boq.revised, …
    print(event["event_type"], event["payload"])
```
CLI: `python -m construct_dpr --help`. Tests: `python -m unittest discover -s tests`. Full guide: `docs/INTEGRATIONS.md`.
