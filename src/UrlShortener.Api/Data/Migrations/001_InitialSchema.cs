using FluentMigrator;

namespace UrlShortener.Api.Data.Migrations;

[Migration(1, "Initial schema")]
public class M001_InitialSchema : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS users (
                id          BIGSERIAL PRIMARY KEY,
                email       TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS short_urls (
                id          BIGSERIAL PRIMARY KEY,
                code        TEXT NOT NULL UNIQUE,
                target_url  TEXT NOT NULL,
                user_id     BIGINT NOT NULL REFERENCES users(id),
                is_active   BOOLEAN NOT NULL DEFAULT true,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_short_urls_code ON short_urls(code);
            CREATE INDEX IF NOT EXISTS ix_short_urls_user_id ON short_urls(user_id);

            CREATE TABLE IF NOT EXISTS click_events (
                id          BIGSERIAL PRIMARY KEY,
                short_url_id BIGINT NOT NULL REFERENCES short_urls(id),
                clicked_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                user_agent  TEXT,
                ip_address  TEXT
            );

            CREATE TABLE IF NOT EXISTS url_stats (
                short_url_id BIGINT PRIMARY KEY REFERENCES short_urls(id),
                click_count  BIGINT NOT NULL DEFAULT 0,
                last_clicked_at TIMESTAMPTZ
            );

            CREATE TABLE IF NOT EXISTS request_timings (
                id           BIGSERIAL PRIMARY KEY,
                path         TEXT NOT NULL,
                method       TEXT NOT NULL,
                status_code  INT NOT NULL,
                elapsed_ms   DOUBLE PRECISION NOT NULL,
                recorded_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );
        ");
    }

    public override void Down()
    {
        Execute.Sql(@"
            DROP TABLE IF EXISTS request_timings;
            DROP TABLE IF EXISTS url_stats;
            DROP TABLE IF EXISTS click_events;
            DROP INDEX IF EXISTS ix_short_urls_user_id;
            DROP INDEX IF EXISTS ix_short_urls_code;
            DROP TABLE IF EXISTS short_urls;
            DROP TABLE IF EXISTS users;
        ");
    }
}
