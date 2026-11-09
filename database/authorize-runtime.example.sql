-- Run separately as an Entra-authorized database administrator after DACPAC publication.
-- Replace the identity name with the application's user-assigned managed identity name.
CREATE USER [REPLACE_WITH_RUNTIME_IDENTITY] FROM EXTERNAL PROVIDER;
GRANT SELECT, INSERT, UPDATE ON OBJECT::[sync].[Cases] TO [REPLACE_WITH_RUNTIME_IDENTITY];
GRANT SELECT ON OBJECT::[sync].[Health] TO [REPLACE_WITH_RUNTIME_IDENTITY];
GRANT SELECT ON OBJECT::[sync].[Configuration] TO [REPLACE_WITH_RUNTIME_IDENTITY];
GRANT SELECT, INSERT, UPDATE ON OBJECT::[sync].[Runs] TO [REPLACE_WITH_RUNTIME_IDENTITY];
-- No schema/DDL permissions. Observer identities need only SELECT on sync.Health.

-- Separate web identity: preview has no destination credentials and never mutates sync.Cases or sync.Runs.
CREATE USER [REPLACE_WITH_WEB_IDENTITY] FROM EXTERNAL PROVIDER;
GRANT SELECT ON OBJECT::[sync].[Cases] TO [REPLACE_WITH_WEB_IDENTITY];
GRANT SELECT ON OBJECT::[sync].[Runs] TO [REPLACE_WITH_WEB_IDENTITY];
GRANT SELECT, INSERT, UPDATE ON OBJECT::[sync].[Configuration] TO [REPLACE_WITH_WEB_IDENTITY];
GRANT INSERT ON OBJECT::[sync].[AccessAudit] TO [REPLACE_WITH_WEB_IDENTITY];
