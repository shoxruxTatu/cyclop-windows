-- One row per install per day it checked in. The id is a random number the
-- app made on first launch; nothing in this table says who or where.
CREATE TABLE IF NOT EXISTS pings (
  id      TEXT NOT NULL,
  day     TEXT NOT NULL,          -- YYYY-MM-DD, UTC
  version TEXT NOT NULL,
  arch    TEXT NOT NULL,
  os      TEXT NOT NULL,
  PRIMARY KEY (id, day)
);
CREATE INDEX IF NOT EXISTS pings_day ON pings (day);
