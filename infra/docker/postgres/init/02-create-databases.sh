#!/bin/bash
# One database per service, owned by that service's role and closed to
# every other role.
#
# This script is the whole point of stage 2. See docs/adr/0003.

set -euo pipefail

SERVICES="identity tenant subscription property pricing guest booking \
          payment operations notification reporting audit content"

echo "=== creating service databases ==="

for svc in ${SERVICES}; do
    db="hs_${svc}"
    role="hs_${svc}_user"

    psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname postgres <<-SQL
        create database ${db} owner ${role};

        -- PostgreSQL grants CONNECT on every new database to PUBLIC.
        -- Without this revoke, hs_booking_user could connect to
        -- hs_property and read every row in it. This single line is what
        -- turns "do not query another service's database" from a
        -- code-review convention into an error from the database.
        revoke connect on database ${db} from public;
        grant  connect on database ${db} to ${role};
SQL

    # pg_stat_statements has to be created inside each database.
    # Used from stage 10 onwards to see what the concurrency work costs.
    psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${db}" <<-SQL
        create extension if not exists pg_stat_statements;

        -- The owner already has full rights on its own database; this
        -- just makes sure nothing else does.
        revoke all on schema public from public;
        grant  all on schema public to ${role};
SQL

    echo "  ${db} -> owner ${role}, closed to everyone else"
done

echo "=== databases done ==="
echo
echo "Isolation check (expected: permission denied for database hs_property)"
if PGPASSWORD="${HS_SERVICE_PASSWORD}" psql \
        --username hs_booking_user \
        --host 127.0.0.1 \
        --dbname hs_property \
        --command 'select 1' >/dev/null 2>&1; then
    echo "  FAILED - hs_booking_user CAN reach hs_property"
    exit 1
else
    echo "  OK - hs_booking_user cannot reach hs_property"
fi
