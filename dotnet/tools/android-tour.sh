#!/usr/bin/env bash
# Installs the APK on a running emulator, starts the app's screenshot tour and captures each screen.
# Usage: tools/android-tour.sh <apk> <output-dir>
set -u
APK="$1"; OUT="$2"
mkdir -p "$OUT"
adb wait-for-device
adb install -r "$APK"
adb logcat -c
adb shell am start -W -n com.sitenotes.dpr/com.sitenotes.dpr.MainActivity --ez tour true
sleep 25
adb exec-out screencap -p > "$OUT/00-startup.png"
end=$((SECONDS + 480))
seen=""
while [ $SECONDS -lt $end ]; do
  line=$(adb logcat -d 2>/dev/null | grep -o 'SITENOTES_TOUR [A-Z]* [0-9a-z_-]*' | tail -1 || true)
  if [[ "$line" == *DONE* ]]; then echo "tour finished: $line"; break; fi
  if [[ "$line" == *READY* && "$line" != "$seen" ]]; then
    seen="$line"
    name=$(echo "$line" | awk '{print $3}')
    adb exec-out screencap -p > "$OUT/$name.png" && echo "captured $name"
  fi
  sleep 0.5
done
echo "----- app log -----"
adb logcat -d | grep -E "SITENOTES_TOUR|SITENOTES_ERROR|SiteNotes|FATAL|AndroidRuntime|DOTNET|monodroid|mono-rt" | tail -150 | tee "$OUT/tour-log.txt"
ls -la "$OUT"
# a tour that captured almost nothing means the app crashed or never came to the front: fail loudly
count=$(ls "$OUT"/*.png 2>/dev/null | wc -l)
echo "screens captured: $count"
if [ "$count" -lt 12 ]; then echo "::error::Android screen tour captured only $count screens – see tour-log.txt"; exit 1; fi
