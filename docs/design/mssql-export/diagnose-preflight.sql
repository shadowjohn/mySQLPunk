-- mySQLPunk MSSQL preflight diagnostics (read-only).
-- Execute with the SAME login and selected database as the export wizard.
-- No application row data, property values, module definitions or credentials are returned.
-- No database settings, users, permissions or objects are changed.
SET NOCOUNT ON;

-- 1. Environment and effective database permissions.
SELECT DB_NAME() AS database_name,
       CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
       CONVERT(nvarchar(128), SERVERPROPERTY('Edition')) AS edition,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONNECT') AS can_connect,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS can_view_database_definition,
       HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') AS can_alter_database;
SELECT DB_NAME() AS database_name, d.snapshot_isolation_state,
       d.snapshot_isolation_state_desc, d.is_read_committed_snapshot_on
FROM (SELECT DB_ID() AS database_id) AS current_db
LEFT JOIN sys.databases AS d ON d.database_id = current_db.database_id;

-- A hidden catalog is NOT an empty catalog. Do not infer object absence from it.
IF ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) <> 1
BEGIN
    SELECT N'METADATA_NOT_VISIBLE' AS diagnostic_status,
           N'Database VIEW DEFINITION is unavailable. Have a DBA run the remaining metadata queries; an empty result under this login would not prove absence.' AS diagnostic_message;
    RETURN;
END;

-- 2. Actual reasons behind the aggregated table/view feature message.
SELECT s.name AS schema_name, o.name AS object_name, o.type_desc,
       HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), 'OBJECT', 'SELECT') AS can_select,
       (SELECT COUNT(*) FROM sys.indexes AS i WHERE i.object_id = o.object_id AND i.index_id > 0) AS index_count,
       (SELECT COUNT(*) FROM sys.triggers AS t WHERE t.parent_id = o.object_id) AS trigger_count,
       (SELECT COUNT(*) FROM sys.extended_properties AS e WHERE e.class = 1 AND e.major_id = o.object_id) AS property_count,
       (SELECT COUNT(*) FROM sys.extended_properties AS e WHERE e.class = 1 AND e.major_id = o.object_id AND e.name <> N'MS_Description') AS non_description_property_count
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE s.name = N'dbo' AND o.name IN
 (N'WRA_WATERLEVEL_STATION', N'V_EMIC_DISASTER', N'View_WRA_RESERVOIR',
  N'View_WRA_WATERLEVEL', N'V_CWB_RAIN', N'Vw_LatestWaterLevel_WithStation_AndCounty')
ORDER BY o.name;

-- 3. Property ownership, names and SQL types only; values are deliberately excluded.
SELECT s.name AS schema_name, o.name AS object_name, o.type_desc,
       e.minor_id, c.name AS column_name, e.name AS property_name,
       CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY(e.value, 'BaseType')) AS property_base_type,
       CONVERT(int, SQL_VARIANT_PROPERTY(e.value, 'MaxLength')) AS property_max_length,
       CONVERT(int, SQL_VARIANT_PROPERTY(e.value, 'Precision')) AS property_precision,
       CONVERT(int, SQL_VARIANT_PROPERTY(e.value, 'Scale')) AS property_scale,
       CONVERT(nvarchar(128), SQL_VARIANT_PROPERTY(e.value, 'Collation')) AS property_collation,
       DATALENGTH(e.value) AS property_bytes
FROM sys.extended_properties AS e
JOIN sys.objects AS o ON o.object_id = e.major_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.columns AS c ON c.object_id = o.object_id AND c.column_id = e.minor_id
WHERE e.class = 1 AND s.name = N'dbo' AND o.name IN
 (N'WRA_WATERLEVEL_STATION', N'V_EMIC_DISASTER', N'View_WRA_RESERVOIR',
  N'View_WRA_WATERLEVEL', N'V_CWB_RAIN', N'Vw_LatestWaterLevel_WithStation_AndCounty')
ORDER BY o.name, e.minor_id, e.name;

-- 4. Index / trigger identities: a property-only view must not be reported as indexed.
SELECT s.name AS schema_name, o.name AS object_name, N'INDEX' AS feature_kind,
       i.name AS feature_name, i.type_desc AS feature_type
FROM sys.views AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.indexes AS i ON i.object_id = o.object_id AND i.index_id > 0
WHERE s.name = N'dbo' AND o.name IN
 (N'V_EMIC_DISASTER', N'View_WRA_RESERVOIR', N'View_WRA_WATERLEVEL', N'V_CWB_RAIN', N'Vw_LatestWaterLevel_WithStation_AndCounty')
UNION ALL
SELECT s.name, o.name, N'TRIGGER', t.name, t.type_desc
FROM sys.views AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.triggers AS t ON t.parent_id = o.object_id
WHERE s.name = N'dbo' AND o.name IN
 (N'V_EMIC_DISASTER', N'View_WRA_RESERVOIR', N'View_WRA_WATERLEVEL', N'V_CWB_RAIN', N'Vw_LatestWaterLevel_WithStation_AndCounty');

-- 5. Database-qualified dependencies and locally resolvable object identity.
-- VIEW DEFINITION alone does not imply SELECT on this catalog view.
IF ISNULL(HAS_PERMS_BY_NAME(N'sys.sql_expression_dependencies', N'OBJECT', N'SELECT'), 0) <> 1
BEGIN
    SELECT N'DEPENDENCY_CATALOG_NOT_READABLE' AS diagnostic_status,
           N'SELECT on sys.sql_expression_dependencies is unavailable. Have a DBA review access; dependency results are unknown, not empty.' AS diagnostic_message;
    RETURN;
END;
SELECT s.name AS referencing_schema, o.name AS referencing_object,
       d.referenced_server_name, d.referenced_database_name,
       d.referenced_schema_name, d.referenced_entity_name, d.referenced_id,
       CASE WHEN d.referenced_server_name IS NULL
                 AND (d.referenced_database_name IS NULL OR DB_ID(d.referenced_database_name) = DB_ID())
            THEN 1 ELSE 0 END AS same_database_reference,
       CASE WHEN d.referenced_server_name IS NULL
                 AND (d.referenced_database_name IS NULL OR DB_ID(d.referenced_database_name) = DB_ID())
                 AND d.referenced_schema_name IS NOT NULL
            THEN OBJECT_ID(QUOTENAME(d.referenced_schema_name) + N'.' + QUOTENAME(d.referenced_entity_name))
            ELSE d.referenced_id END AS locally_resolved_object_id
FROM sys.sql_expression_dependencies AS d
JOIN sys.objects AS o ON o.object_id = d.referencing_id
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE d.referenced_class = 1 AND s.name = N'dbo' AND o.name IN
 (N'V_EMIC_DISASTER', N'View_WRA_RESERVOIR', N'View_WRA_WATERLEVEL', N'V_CWB_RAIN', N'Vw_LatestWaterLevel_WithStation_AndCounty')
ORDER BY o.name, d.referenced_database_name, d.referenced_schema_name, d.referenced_entity_name;
