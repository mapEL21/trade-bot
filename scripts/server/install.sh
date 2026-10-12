#!/usr/bin/env bash
# Installs or updates Trade Bot from the unpacked release next to this script. Safe to run again for every update:
# settings in /etc/trade-bot/trade-bot.env and data in /var/lib/trade-bot are kept.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
if [ "$(id -u)" -ne 0 ]; then echo "Запустите через sudo: sudo bash $0"; exit 1; fi
if [ ! -x "$here/app/TradeBot.Api" ] && [ ! -f "$here/app/TradeBot.Api" ]; then echo "Рядом нет папки app с собранным сервером."; exit 1; fi

echo "Проверяем системные библиотеки..."
if command -v apt-get >/dev/null; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ca-certificates curl tzdata >/dev/null
  # .NET needs ICU for Russian number and date formats.
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libicu74 >/dev/null 2>&1 \
    || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libicu-dev >/dev/null
fi

id trade-bot >/dev/null 2>&1 || useradd --system --home-dir /var/lib/trade-bot --shell /usr/sbin/nologin trade-bot
install -d -o trade-bot -g trade-bot -m 750 /var/lib/trade-bot
install -d -m 755 /etc/trade-bot
if [ ! -f /etc/trade-bot/trade-bot.env ]; then
  install -m 640 -g trade-bot "$here/trade-bot.env" /etc/trade-bot/trade-bot.env
  echo "Создан файл настроек /etc/trade-bot/trade-bot.env"
fi

# A stop lets the running server finish its recordings and save the scanner journal.
if systemctl is-active --quiet trade-bot; then echo "Останавливаем работающий сервер..."; systemctl stop trade-bot; fi
rm -rf /opt/trade-bot.new
cp -r "$here/app" /opt/trade-bot.new
chmod 755 /opt/trade-bot.new/TradeBot.Api
rm -rf /opt/trade-bot.previous
if [ -d /opt/trade-bot ]; then mv /opt/trade-bot /opt/trade-bot.previous; fi
mv /opt/trade-bot.new /opt/trade-bot

install -m 644 "$here/trade-bot.service" /etc/systemd/system/trade-bot.service
systemctl daemon-reload
systemctl enable --quiet trade-bot
systemctl start trade-bot

echo "Ждём запуска..."
for _ in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:5080/api/v1/health >/dev/null 2>&1; then
    echo "Trade Bot работает. Журнал: sudo journalctl -u trade-bot -f"
    exit 0
  fi
  sleep 1
done
echo "Сервер не ответил за 30 секунд. Журнал: sudo journalctl -u trade-bot -n 100"
exit 1
