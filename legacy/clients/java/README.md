# CONSTRUCT DPR Java client

Java 11+, no dependencies (uses `java.net.http`). Responses are JSON strings — parse them with the JSON library your system already uses.

```bash
./build.sh test            # builds target/construct-dpr-client.jar and runs the tests
# or: mvn package
```
```java
var api = new ConstructDprClient("https://xyz.supabase.co", anonKey);
api.signIn("erp-bot@company.com", password);   // account with the "integration" project role
String progress = api.select("activity_progress", Map.of("project_id", "eq." + projectId));
String events = api.events(lastId, 500);
```
Full guide: `docs/INTEGRATIONS.md`.
