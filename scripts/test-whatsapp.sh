#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
TEST_DIR="$(mktemp -d "${TMPDIR:-/tmp}/lazo-whatsapp.XXXXXX")"
trap 'rm -rf "$TEST_DIR"' EXIT
swiftc NotchBuddy/Sources/App/WhatsAppModels.swift \
    NotchBuddy/Sources/App/WhatsAppGateway.swift \
    tests/WhatsAppTests.swift tests/WhatsAppGatewayTests.swift \
    -o "$TEST_DIR/whatsapp-tests"
"$TEST_DIR/whatsapp-tests"
