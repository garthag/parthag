#!/usr/bin/env bash
# Builds the client jar with plain javac (no downloads). Usage: ./build.sh [test]
set -euo pipefail
cd "$(dirname "$0")"
rm -rf target && mkdir -p target/classes target/test-classes
javac --release 11 -d target/classes $(find src/main/java -name '*.java')
jar --create --file target/construct-dpr-client.jar -C target/classes .
echo "Built target/construct-dpr-client.jar"
if [[ "${1:-}" == "test" ]]; then
  javac --release 11 -cp target/classes -d target/test-classes $(find src/test/java -name '*.java')
  java -cp target/classes:target/test-classes org.constructdpr.client.ClientTest
fi
