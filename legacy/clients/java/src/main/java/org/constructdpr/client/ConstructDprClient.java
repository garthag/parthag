package org.constructdpr.client;

import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.StringJoiner;

/**
 * Java 11+ client for the CONSTRUCT DPR API (Supabase REST + Auth).
 *
 * <p>Sign in with a dedicated account that has the project role
 * {@code integration}. Row Level Security applies to every call. Never use the
 * Supabase service-role key in an integration: it bypasses all permissions.
 *
 * <pre>{@code
 * var api = new ConstructDprClient("https://xyz.supabase.co", anonKey);
 * api.signIn("erp-bot@company.com", password);
 * String projects = api.select("projects", Map.of("code", "eq.BF2"));
 * }</pre>
 * Responses are JSON strings; parse them with your preferred JSON library.
 */
public class ConstructDprClient {
    private final String url;
    private final String anonKey;
    private final HttpClient http;
    private final int retries;
    private String accessToken;
    private String refreshToken;
    private Instant expiresAt = Instant.EPOCH;

    public ConstructDprClient(String url, String anonKey) {
        this(url, anonKey, 3);
    }

    public ConstructDprClient(String url, String anonKey, int retries) {
        this.url = url.replaceAll("/+$", "");
        this.anonKey = anonKey;
        this.retries = retries;
        this.http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(15)).build();
    }

    // ---------------------------------------------------------------- auth
    public void signIn(String email, String password) {
        String body = Json.object(Map.of("email", email, "password", password));
        storeSession(send("POST", "/auth/v1/token?grant_type=password", body, Map.of(), false));
    }

    private void refresh() {
        if (refreshToken == null) throw new ConstructDprException(401, "Session expired; sign in again", null);
        String body = Json.object(Map.of("refresh_token", refreshToken));
        storeSession(send("POST", "/auth/v1/token?grant_type=refresh_token", body, Map.of(), false));
    }

    private void storeSession(String json) {
        accessToken = Json.stringField(json, "access_token");
        refreshToken = Json.stringField(json, "refresh_token");
        Long exp = Json.numberField(json, "expires_in");
        expiresAt = Instant.now().plusSeconds(exp == null ? 3600 : exp);
        if (accessToken == null) throw new ConstructDprException(500, "No access token in response", null);
    }

    public String getAccessToken() { return accessToken; }

    // ---------------------------------------------------------- operations
    /** PostgREST read. Filters use PostgREST syntax, e.g. {@code Map.of("status", "eq.approved")}. */
    public String select(String table, Map<String, String> filters) {
        return select(table, filters, "*", null, null);
    }

    public String select(String table, Map<String, String> filters, String columns, String order, Integer limit) {
        Map<String, String> q = new LinkedHashMap<>();
        q.put("select", columns);
        if (filters != null) q.putAll(filters);
        if (order != null) q.put("order", order);
        if (limit != null) q.put("limit", limit.toString());
        return send("GET", "/rest/v1/" + table + "?" + query(q), null, Map.of(), true);
    }

    /** Calls a database function, e.g. {@code rpc("dpr_document", "{\"p_id\":\"…\"}")}. */
    public String rpc(String function, String jsonArgs) {
        return send("POST", "/rest/v1/rpc/" + function, jsonArgs == null ? "{}" : jsonArgs, Map.of(), true);
    }

    /** Insert-or-update rows (a JSON array) using the given unique columns. */
    public String upsert(String table, String jsonArray, String onConflict) {
        return send("POST", "/rest/v1/" + table + "?on_conflict=" + enc(onConflict), jsonArray,
                Map.of("Prefer", "resolution=merge-duplicates,return=representation"), true);
    }

    public String projects() { return select("projects", Map.of(), "*", "code", null); }

    public String dprDocument(String dprId) { return rpc("dpr_document", Json.object(Map.of("p_id", dprId))); }

    public String exportProject(String projectId) { return rpc("export_project", Json.object(Map.of("p_project", projectId))); }

    public String boqStatus(String projectId) { return select("boq_status", Map.of("project_id", "eq." + projectId), "*", "item_no", null); }

    /** Events with id greater than {@code afterId}, oldest first. Store the last id you processed as your cursor. */
    public String events(long afterId, int limit) {
        return select("integration_events", Map.of("id", "gt." + afterId), "*", "id", limit);
    }

    // ----------------------------------------------------------- transport
    private String send(String method, String path, String body, Map<String, String> extra, boolean auth) {
        if (auth && accessToken == null) throw new ConstructDprException(401, "Call signIn() first", null);
        if (auth && refreshToken != null && Instant.now().isAfter(expiresAt.minusSeconds(60))) refresh();
        for (int attempt = 0; ; attempt++) {
            HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(url + path))
                    .timeout(Duration.ofSeconds(60))
                    .header("apikey", anonKey)
                    .header("Accept", "application/json");
            if (auth) b.header("Authorization", "Bearer " + accessToken);
            extra.forEach(b::header);
            if (body != null) b.header("Content-Type", "application/json").method(method, HttpRequest.BodyPublishers.ofString(body));
            else b.method(method, HttpRequest.BodyPublishers.noBody());
            HttpResponse<String> res;
            try {
                res = http.send(b.build(), HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
            } catch (IOException e) {
                if (attempt < retries) { sleep(attempt); continue; }
                throw new ConstructDprException(0, "Network error: " + e.getMessage(), null);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new ConstructDprException(0, "Interrupted", null);
            }
            int s = res.statusCode();
            if (s < 300) return res.body();
            String msg = firstNonNull(Json.stringField(res.body(), "message"), Json.stringField(res.body(), "error_description"),
                    Json.stringField(res.body(), "msg"), res.body());
            if (s == 401 && auth && refreshToken != null && attempt == 0 && msg.contains("JWT")) { refresh(); continue; }
            if ((s == 429 || s == 502 || s == 503 || s == 504) && attempt < retries) { sleep(attempt); continue; }
            throw new ConstructDprException(s, msg, Json.stringField(res.body(), "code"));
        }
    }

    private static String firstNonNull(String... v) {
        for (String s : v) if (s != null) return s;
        return "";
    }

    private static void sleep(int attempt) {
        try { Thread.sleep(1000L << attempt); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
    }

    private static String enc(String s) { return URLEncoder.encode(s, StandardCharsets.UTF_8).replace("%2C", ",").replace("%2E", "."); }

    private static String query(Map<String, String> q) {
        StringJoiner j = new StringJoiner("&");
        q.forEach((k, v) -> j.add(enc(k) + "=" + enc(v)));
        return j.toString();
    }
}
