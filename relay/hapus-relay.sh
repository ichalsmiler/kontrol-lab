#!/bin/bash
# Menghapus relay Kontrol Lab dari server ini (layanan, kode, dan token). Jalankan sebagai root:
#   curl -fsSL https://raw.githubusercontent.com/ichalsmiler/kontrol-lab/main/relay/hapus-relay.sh | bash
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "Jalankan sebagai root."; exit 1; }
systemctl disable --now kontrol-lab-relay 2>/dev/null || true
rm -f /etc/systemd/system/kontrol-lab-relay.service
systemctl daemon-reload
rm -rf /opt/kontrol-lab-relay
echo "Relay Kontrol Lab dihapus. Node.js tidak ikut dihapus."
