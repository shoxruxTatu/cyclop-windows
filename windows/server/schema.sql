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

-- Downloads through the website's button, per day and architecture.
CREATE TABLE IF NOT EXISTS downloads (
  day  TEXT NOT NULL,
  arch TEXT NOT NULL,
  n    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, arch)
);

-- The last release lookup that worked, per platform — used when GitHub does
-- not answer a lookup, so a download button always gets a file.
CREATE TABLE IF NOT EXISTS known (
  name  TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- Bug reports and suggestions from the app and the website.
CREATE TABLE IF NOT EXISTS feedback (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  created TEXT NOT NULL,              -- ISO time, UTC
  kind    TEXT NOT NULL,              -- bug | idea
  text    TEXT NOT NULL,
  contact TEXT NOT NULL DEFAULT '',   -- what the sender chose to leave, if anything
  version TEXT NOT NULL DEFAULT '',
  os      TEXT NOT NULL DEFAULT '',
  arch    TEXT NOT NULL DEFAULT '',
  source  TEXT NOT NULL,              -- app | site
  log     TEXT NOT NULL DEFAULT '',   -- the app's log, only when the sender attached it
  status  TEXT NOT NULL DEFAULT 'new' -- new | done
);
CREATE INDEX IF NOT EXISTS feedback_status ON feedback (status, id);
