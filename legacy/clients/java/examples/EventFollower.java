import java.nio.file.Files;
import java.nio.file.Path;
import org.constructdpr.client.ConstructDprClient;

/**
 * Polls the event feed every 30 s and prints new events (replace with your ERP call).
 * The cursor file makes it restart-safe. Credentials come from environment variables.
 *
 *   java -cp construct-dpr-client.jar:. EventFollower
 */
public class EventFollower {
    public static void main(String[] args) throws Exception {
        var api = new ConstructDprClient(System.getenv("CDPR_URL"), System.getenv("CDPR_ANON_KEY"));
        api.signIn(System.getenv("CDPR_EMAIL"), System.getenv("CDPR_PASSWORD"));
        Path cursorFile = Path.of("events.cursor");
        long cursor = Files.exists(cursorFile) ? Long.parseLong(Files.readString(cursorFile).trim()) : 0;
        while (true) {
            String batch = api.events(cursor, 500);       // JSON array, oldest first
            // Parse with your JSON library; for each event handle it, then advance the cursor:
            java.util.regex.Matcher m = java.util.regex.Pattern.compile("\"id\":(\\d+)").matcher(batch);
            boolean any = false;
            while (m.find()) { cursor = Long.parseLong(m.group(1)); any = true; }
            if (any) {
                System.out.println(batch);
                Files.writeString(cursorFile, Long.toString(cursor));
            } else {
                Thread.sleep(30_000);
            }
        }
    }
}
