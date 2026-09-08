#!/bin/bash
# One login role per service. Created BEFORE the databases, because each
# database is created "owner hs_<service>_user".
#
# Runs once, on first start of an empty pgdata volume.

set -euo pipefail

SERVICES="identity tenant subscription property pricing guest booking \
          payment operations notification reporting audit content"
# hs_stay is deliberately absent - stage 14 creates it when the stay
# module is extracted out of booking. Until then stay lives in a separate
# schema inside hs_booking.

echo "=== creating service roles ==="

for svc in ${SERVICES}; do
    role="hs_${svc}_user"

    psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname postgres <<-SQL
        create role ${role}
            with login
                 password '${HS_SERVICE_PASSWORD}'
                 nosuperuser
                 nocreatedb
                 nocreaterole
                 noinherit
                 nobypassrls;
SQL

    echo "  role ${role}"
done

echo "=== ${#SERVICES} roles done ==="
