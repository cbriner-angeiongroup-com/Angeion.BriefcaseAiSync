-- Run only as the SQL Entra administrator against the new application database.
-- This is deployment access, never runtime access. No shared server administrator is changed.
IF DB_NAME() <> N'BriefcaseSyncDev'
    THROW 51000, 'WRONG_DEVELOPMENT_DATABASE', 1;

IF DATABASE_PRINCIPAL_ID(N'Angeion.BriefcaseAiSync-development-deploy') IS NULL
    CREATE USER [Angeion.BriefcaseAiSync-development-deploy] FROM EXTERNAL PROVIDER;
-- The dedicated publisher can publish the DACPAC and apply contained-user grants in this database only.
ALTER ROLE db_owner ADD MEMBER [Angeion.BriefcaseAiSync-development-deploy];

-- Interactive first publication can instead be performed by the SQL administrator.
-- Worker and web permissions are separately applied by authorize-runtime.example.sql
-- or eng/AuthorizeDevelopmentSql; neither runtime identity receives db_owner or DDL grants.
