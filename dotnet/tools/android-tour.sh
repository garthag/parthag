#!/usr/bin/env bash
# Installs the APK on a running emulator, starts the app's screenshot tour and captures each screen.
# Usage: tools/android-tour.sh <apk> <output-dir>
set -u
APK="$1"; OUT="$2"
mkdir -p "$OUT"
adb wait-for-device
adb install -r "$APK"
adb logcat -c
adb shell am start -n com.sitenotes.dpr/com.sitenotes.dpr.MainActivity --ez tour true
end=$((SECONDS + 420))
seen=""
while [ $SECONDS -lt $end ]; do
  line=$(adb logcat -d 2>/dev/null | grep -o 'SITENOTES_TOUR [A-Z]* [0-9a-z_-]*' | tail -1 || true)
  if [[ "$line" == *DONE* ]]; then echo "tour finished: $line"; break; fi
  if [[ -n "$line" && "$line" != "$seen" ]]; then
    seen="$line"
    name=$(echo "$line" | awk '{print $3}')
    adb exec-out screencap -p > "$OUT/$name.png" && echo "captured $name"
  fi
  sleep 0.5
done
adb logcat -d | grep -E "SITENOTES_TOUR|FATAL|AndroidRuntime" | tail -80 > "$OUT/tour-log.txt" || true
ls -la "$OUT"
