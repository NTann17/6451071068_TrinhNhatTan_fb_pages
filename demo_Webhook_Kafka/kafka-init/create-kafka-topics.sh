#!/bin/bash
set -euo pipefail

echo "Waiting for Kafka broker to become ready..."
until kafka-topics --bootstrap-server kafka:9092 --list >/dev/null 2>&1; do
  sleep 2
done

for topic in raw_events reply_commands send_retry send_failed dead_letter; do
  kafka-topics --bootstrap-server kafka:9092 --create --if-not-exists --topic "$topic" --partitions 1 --replication-factor 1 >/dev/null
  echo "Ensured topic: $topic"
done

echo "Kafka topics are ready."