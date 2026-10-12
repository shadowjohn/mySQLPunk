-- Separate unsupported selection; never mix into the positive Core selection.
CREATE TABLE dbo.RejectSpatial(g geometry,h geography);
CREATE TABLE dbo.RejectVariant(v sql_variant);
CREATE TYPE dbo.RejectAlias FROM int NULL;
GO
CREATE TABLE dbo.RejectAliasTable(v dbo.RejectAlias);
GO
CREATE XML SCHEMA COLLECTION dbo.RejectXsd AS N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="r" type="xs:string"/></xs:schema>';
GO
CREATE TABLE dbo.RejectTypedXml(x xml(dbo.RejectXsd));
GO
CREATE VIEW dbo.RejectEncrypted WITH ENCRYPTION AS SELECT 1 AS v;
GO
CREATE SEQUENCE dbo.RejectSequence AS int START WITH 1;
CREATE TABLE dbo.RejectSequenceDependency(v int DEFAULT NEXT VALUE FOR dbo.RejectSequence);
-- Separate fresh fixture: SNAPSHOT OFF is a DB setting, not a table in Core.
-- Separate minimally privileged reader: DENY VIEW DEFINITION must yield preflight failure.
-- Temporal, special indexes, CLR/FILESTREAM/encryption/version compatibility are pending
-- capability-specific phases; do not enable server features to fabricate these here.
