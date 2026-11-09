package org.constructdpr.client;

import java.util.Map;

/**
 * Tiny JSON helpers so the client has no dependencies. Responses are returned
 * as raw JSON strings: parse them with the library your system already uses
 * (Jackson, Gson, org.json …).
 */
public final class Json {
    private Json() {}

    public static String quote(String s) {
        if (s == null) return "null";
        StringBuilder b = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
                    else b.append(c);
            }
        }
        return b.append('"').toString();
    }

    /** Builds a JSON object from String / Number / Boolean / null values or pre-built JSON (wrap with {@link Raw}). */
    public static String object(Map<String, ?> values) {
        StringBuilder b = new StringBuilder("{");
        boolean first = true;
        for (Map.Entry<String, ?> e : values.entrySet()) {
            if (!first) b.append(',');
            first = false;
            b.append(quote(e.getKey())).append(':').append(value(e.getValue()));
        }
        return b.append('}').toString();
    }

    static String value(Object v) {
        if (v == null) return "null";
        if (v instanceof Raw) return ((Raw) v).json;
        if (v instanceof Number || v instanceof Boolean) return v.toString();
        return quote(v.toString());
    }

    /** Extracts a top-level string field, e.g. "access_token" (enough for the auth responses). */
    static String stringField(String json, String field) {
        String key = quote(field);
        int i = json.indexOf(key);
        if (i < 0) return null;
        int colon = json.indexOf(':', i + key.length());
        int start = json.indexOf('"', colon + 1);
        if (start < 0 || json.substring(colon + 1, start).trim().length() > 0) return null;
        StringBuilder out = new StringBuilder();
        for (int k = start + 1; k < json.length(); k++) {
            char c = json.charAt(k);
            if (c == '\\') { out.append(json.charAt(++k)); continue; }
            if (c == '"') return out.toString();
            out.append(c);
        }
        return null;
    }

    static Long numberField(String json, String field) {
        String key = quote(field);
        int i = json.indexOf(key);
        if (i < 0) return null;
        int k = json.indexOf(':', i + key.length()) + 1;
        while (k < json.length() && Character.isWhitespace(json.charAt(k))) k++;
        int end = k;
        while (end < json.length() && (Character.isDigit(json.charAt(end)) || json.charAt(end) == '-')) end++;
        return end > k ? Long.parseLong(json.substring(k, end)) : null;
    }

    /** Marks a string as already-encoded JSON. */
    public static final class Raw {
        final String json;
        public Raw(String json) { this.json = json; }
    }
}
