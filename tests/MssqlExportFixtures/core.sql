-- Run only after the caller validates its generated *_src DB and ownership token.
-- Database default must be Latin1_General_100_BIN2 (different from tempdb).
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
CREATE SCHEMA [一] AUTHORIZATION dbo;
GO
CREATE SCHEMA [二] AUTHORIZATION dbo;
GO
CREATE TABLE [一].[同名]]表] (
 [id] int IDENTITY(3,2) NOT NULL CONSTRAINT [PK_same] PRIMARY KEY,
 [unique] int NOT NULL CONSTRAINT [UQ_same] UNIQUE,
 [decimal38] decimal(38,18) NULL,
 [offset] datetimeoffset(7) NULL,
 [date] date NULL, [time] time(7) NULL, [dt2] datetime2(7) NULL,
 [raw] varchar(max) COLLATE Chinese_Taiwan_Stroke_CI_AS NULL,
 [unicode] nvarchar(max) NULL,
 [fixed] char(5) COLLATE Chinese_Taiwan_Stroke_CI_AS NULL,
 [binary] varbinary(max) NULL,
 [defaulted] int NOT NULL CONSTRAINT [DF_same] DEFAULT (7),
 [calc] AS ([unique]+[defaulted]) PERSISTED,
 [rv] rowversion,
 CONSTRAINT [CK_same] CHECK ([unique]>0)
);
CREATE TABLE [二].[同名]]表] ([id] int NOT NULL PRIMARY KEY,[parent] int NULL,
 CONSTRAINT [FK_same] FOREIGN KEY ([parent]) REFERENCES [一].[同名]]表]([id]));
CREATE INDEX [IX_filtered] ON [一].[同名]]表]([unique] DESC,[id] ASC)
 INCLUDE ([defaulted]) WHERE [unique]>1;
INSERT [一].[同名]]表]([unique],[decimal38],[offset],[date],[time],[dt2],[raw],[unicode],[fixed],[binary])
VALUES (1,12345678901234567890.123456789012345678,'2026-10-12T12:34:56.1234567+05:45',
 '0001-01-01','23:59:59.9999999','9999-12-31T23:59:59.9999999',
 '',CONVERT(nvarchar(max),0x41003DD800DE000020002000),NULL,0x0001FF),
 (2,-99999999999999999999.999999999999999999,'2026-10-12T00:00:00.0000001-03:30',
 '9999-12-31','00:00:00.0000001','0001-01-01T00:00:00.0000001','',N'','',0x),
 (3,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
UPDATE [一].[同名]]表] SET [raw].WRITE(0xA4A4002020,NULL,0) WHERE [unique]=1;
UPDATE [一].[同名]]表] SET [fixed]=0xA4A4202020 WHERE [unique]=1;
INSERT [二].[同名]]表] VALUES (1,3);
EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=N'table comment: ] 中文',
 @level0type=N'SCHEMA',@level0name=N'一',@level1type=N'TABLE',@level1name=N'同名]表';
EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=N'column comment: '' and trailing  ',
 @level0type=N'SCHEMA',@level0name=N'一',@level1type=N'TABLE',@level1name=N'同名]表',
 @level2type=N'COLUMN',@level2name=N'raw';
CREATE TABLE dbo.IdentityEmpty ([id] int IDENTITY(11,3),v int);
CREATE TABLE dbo.IdentityNegative ([id] int IDENTITY(-5,-2),v int);
INSERT dbo.IdentityNegative(v) VALUES (1),(2);
CREATE TABLE dbo.IdentityReseed ([id] int IDENTITY(1,1),v int);
INSERT dbo.IdentityReseed(v) VALUES (1);
DBCC CHECKIDENT ('dbo.IdentityReseed',RESEED,100) WITH NO_INFOMSGS;
CREATE TABLE dbo.IdentityDeleted ([id] int IDENTITY(10,2),v int);
INSERT dbo.IdentityDeleted(v) VALUES (1);
DELETE dbo.IdentityDeleted;
CREATE TABLE dbo.IdentityTruncated ([id] int IDENTITY(20,4),v int);
INSERT dbo.IdentityTruncated(v) VALUES (1);
TRUNCATE TABLE dbo.IdentityTruncated;
-- No PK: row ordinal is test-side identity; compare rows as a multiset.
CREATE TABLE dbo.MultiLob (tag int, b varbinary(max),n nvarchar(max),v varchar(max)
 COLLATE Chinese_Taiwan_Stroke_CI_AS,t text COLLATE Chinese_Taiwan_Stroke_CI_AS,
 nt ntext,i image,x xml,wkt nvarchar(max));
DECLARE @legacy TABLE(v varchar(max) COLLATE Chinese_Taiwan_Stroke_CI_AS);
INSERT @legacy VALUES ('');
UPDATE @legacy SET v.WRITE(0xA4A4002020,NULL,0);
INSERT dbo.MultiLob VALUES (1,0x0001FF,CONVERT(nvarchar(max),0x00D800DC00D8),0xA4A4002020,
 (SELECT v FROM @legacy),CONVERT(nvarchar(max),0x41003DD800DE000020002000),0x0001FF,
 CONVERT(xml,N'<r> <a/>  <b> x </b> </r>',1),N'LINESTRING(0 0,1 1)');
INSERT dbo.MultiLob VALUES (2,0x,N'','', '',N'',0x,CONVERT(xml,N'<r/>'),N'GO'+NCHAR(10)+N':r ignored'+NCHAR(10)+N'$(name)');
INSERT dbo.MultiLob(tag) VALUES (3);
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
CREATE VIEW [一].[view]]中文] AS SELECT [id],[unique],[calc] FROM [一].[同名]]表];
GO

CREATE TABLE dbo.LegacySmall(n ntext,i image);
INSERT dbo.LegacySmall VALUES(CONVERT(nvarchar(max),0x00D800DC00D800002000),0x00FF0020),(N'',0x),(NULL,NULL);
