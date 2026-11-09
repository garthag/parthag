package org.constructdpr.client;

/** API error with HTTP status and the server's message (status 0 = network error). */
public class ConstructDprException extends RuntimeException {
    private final int status;
    private final String code;

    public ConstructDprException(int status, String message, String code) {
        super(status + ": " + message);
        this.status = status;
        this.code = code;
    }

    public int getStatus() { return status; }
    public String getCode() { return code; }
    public boolean isConflict() { return status == 409 || getMessage().contains("CONFLICT"); }
}
