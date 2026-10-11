"""Server-side helper for scripts/tools/e2e-upload.ps1. Run it with the SITE's Python
environment (uv run --frozen --project <EQ2Lexicon> python e2e_upload_helper.py ...).

It never prints a connection string or a token, and it only ever talks to a throwaway
database on a LOCAL PostgreSQL server: the one the site's own test suite uses
(TEST_DATABASE_URL in the site's .env), under a different database name.

Subcommands:
  db-create | db-drop          create / drop the scratch database
  migrate                      apply the site's migrations to it
  serve <port>                 run the site against it (blocks)
  token <file>                 mint an API token for a throwaway user, write it to <file>
  readback                     print a JSON summary of what the uploads stored
"""
import asyncio
import json
import os
import subprocess
import sys
from urllib.parse import urlsplit, urlunsplit

SCRATCH_DB = "eq2parser_e2e_upload"
DSN_VARS = ("DATABASE_URL", "SUPABASE_DB_URL", "POSTGRES_CONNECTION_STRING")
LOCAL_HOSTS = ("localhost", "127.0.0.1", "::1")


def site_root() -> str:
    root = os.environ.get("E2E_LEXICON")
    if not root or not os.path.isfile(os.path.join(root, "main.py")):
        raise SystemExit("E2E_LEXICON must name the EQ2Lexicon checkout")
    return root


def local_parts():
    from dotenv import dotenv_values

    values = dotenv_values(os.path.join(site_root(), ".env"))
    test_dsn = values.get("TEST_DATABASE_URL") or os.environ.get("TEST_DATABASE_URL")
    if not test_dsn:
        raise SystemExit("TEST_DATABASE_URL is not set in the site's .env (the local test database)")
    parts = urlsplit(test_dsn)
    if parts.hostname not in LOCAL_HOSTS:
        raise SystemExit("TEST_DATABASE_URL is not a local server; refusing to go on")
    return values, parts


def scratch_dsn() -> str:
    _, parts = local_parts()
    return urlunsplit(parts._replace(path="/" + SCRATCH_DB))


def site_env() -> dict:
    """The site's environment with EVERY database variable pointed at the scratch
    database. The site reads the first of three names that is set and loads its .env
    without overriding what is already in the environment, so setting all three here
    leaves no path to any other database."""
    values, _ = local_parts()
    env = dict(os.environ)
    for key, value in values.items():
        if value is not None and key not in env:
            env[key] = value
    env.pop("TEST_DATABASE_URL", None)
    for var in DSN_VARS:
        env[var] = scratch_dsn()
    env["ENV"] = "dev"
    env["ALLOWED_SERVERS"] = "Varsoon,Wuoshi"
    env["HTTPS_ONLY"] = "false"
    return env


def become_site() -> None:
    env = site_env()
    os.environ.clear()
    os.environ.update(env)
    os.chdir(site_root())
    sys.path.insert(0, site_root())


def db(action: str) -> None:
    import psycopg
    from psycopg import sql

    _, parts = local_parts()
    with psycopg.connect(urlunsplit(parts._replace(path="/postgres")), autocommit=True) as conn:
        if conn.info.host not in LOCAL_HOSTS:
            raise SystemExit("not a local server; refusing")
        conn.execute(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = %s AND pid <> pg_backend_pid()",
            (SCRATCH_DB,),
        )
        conn.execute(sql.SQL("DROP DATABASE IF EXISTS {}").format(sql.Identifier(SCRATCH_DB)))
        if action == "create":
            conn.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(SCRATCH_DB)))
    print(f"{action}: {SCRATCH_DB} on the local server")


def migrate() -> None:
    # The site cannot even be imported against an empty database (its class
    # catalogue is read at import time), so migrations come first.
    become_site()
    from backend import pg_migrate

    print("migrations applied:", len(pg_migrate.run()))


def serve(port: str) -> None:
    env = site_env()
    # --reload is how the site's own dev script starts it on Windows.
    proc = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "backend.server.app:app", "--port", port,
         "--reload", "--reload-dir", "backend", "--timeout-graceful-shutdown", "2"],
        cwd=site_root(), env=env,
    )
    print("server pid", proc.pid, flush=True)
    proc.wait()


def token(path: str) -> None:
    become_site()
    from backend import pg
    from backend.server import db as users_db

    async def main() -> None:
        opened = pg.open_pools()
        if asyncio.iscoroutine(opened):
            await opened
        # open_signup=True makes the new user 'approved'; an unapproved account's token is a 403.
        await users_db.upsert_user("e2e-upload-check", "E2E Check", "e2echeck", None, open_signup=True)
        raw, _row = await users_db.mint_api_token("e2e-upload-check", "e2e upload check")
        with open(path, "w", encoding="ascii") as handle:
            handle.write(raw)
        closed = pg.close_pools()
        if asyncio.iscoroutine(closed):
            await closed

    pg.ensure_selector_event_loop_policy()
    asyncio.run(main())
    print("token minted")


def readback() -> None:
    import psycopg
    from psycopg.rows import dict_row

    with psycopg.connect(scratch_dsn(), row_factory=dict_row) as conn:
        encounters = conn.execute(
            "SELECT world, title, duration_s, success_level, client_warnings FROM parses.encounters ORDER BY id"
        ).fetchall()
        rows = conn.execute(
            "SELECT a.swing_type, a.attack_name, a.damage, a.hits, a.resist, c.name AS attacker "
            "FROM parses.attack_types a JOIN parses.combatants c ON c.id = a.combatant_id ORDER BY a.id"
        ).fetchall()
        combatants = conn.execute("SELECT count(*) AS n FROM parses.combatants").fetchone()["n"]
        quarantined = conn.execute("SELECT count(*) AS n FROM parses.tamper_reports").fetchone()["n"]
    print(json.dumps({
        "encounters": [dict(e) for e in encounters],
        "combatants": combatants,
        "quarantined": quarantined,
        "attack_rows": [dict(r) for r in rows],
    }, default=str))


if __name__ == "__main__":
    command = sys.argv[1]
    if command == "db-create":
        db("create")
    elif command == "db-drop":
        db("drop")
    elif command == "migrate":
        migrate()
    elif command == "serve":
        serve(sys.argv[2])
    elif command == "token":
        token(sys.argv[2])
    elif command == "readback":
        readback()
    else:
        raise SystemExit(__doc__)
