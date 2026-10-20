-- ---------------------------------------------------------------------------
-- 249 — the setup marker that 248 backfilled says when setup completed.
--
-- 248 backfilled system_settings['setup.completed'] on every install that
-- already had a user, and its updated_at took the column default: now(), the
-- moment the migration ran. So the same data migrated twice gave two different
-- rows. The restore drill found it: a backup restored onto this build is
-- migrated at restore time, the database it replaces was migrated at upgrade
-- time, and the two disagreed on that one timestamp and nothing else.
--
-- A migration's result has to depend on the data, not on when it ran. Setup
-- completed when the first real user was created, so that is the value: it is
-- deterministic, and it is what the column means for an install set up after
-- 248, where first-run setup writes the marker in the transaction that creates
-- the first user.
--
-- 248 is on main and is not edited (engineering-standards 3.1); this corrects
-- it. Both ship in the same release, so every install runs them back to back.
-- Re-runnable: it sets the same value every time.
-- ---------------------------------------------------------------------------

UPDATE system_settings s
   SET updated_at = first_user.created_at
  FROM (SELECT min(u.created_at) AS created_at
          FROM users u
         WHERE u.id <> '00000000-0000-0000-0000-000000000001'::uuid) AS first_user
 WHERE s.key = 'setup.completed'
   AND first_user.created_at IS NOT NULL;
