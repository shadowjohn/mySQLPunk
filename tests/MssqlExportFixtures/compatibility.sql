-- Synthetic metadata only; caller owns the generated source database and its token.
-- Source/target defaults are BIN2. Property collation intentionally differs.
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
CREATE SCHEMA [報表] AUTHORIZATION dbo;
GO
CREATE TABLE [報表].[Property] ([id] int NOT NULL PRIMARY KEY, [dropped] int NULL, [note欄] nvarchar(40) NULL);
ALTER TABLE [報表].[Property] DROP COLUMN [dropped];
INSERT [報表].[Property] VALUES (1,N'exported');
GO
CREATE VIEW dbo.PropertyView AS SELECT [id],[note欄] FROM [報表].[Property];
GO
CREATE VIEW dbo.LongView AS SELECT [id] FROM [報表].[Property];
GO
DECLARE @value sql_variant, @short nvarchar(3000)=N'short', @empty nvarchar(40)=N'', @minimum int=-2147483648, @maximum int=2147483647;
SET @value=CONVERT(nvarchar(3503),REPLICATE(N'文',3503)) COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'MS_DiagramPane1',@value=@value,@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'VIEW',@level1name=N'PropertyView';
SET @value=CONVERT(nvarchar(3700),REPLICATE(N'界',3700)) COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'MS_DiagramPane1',@value=@value,@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'VIEW',@level1name=N'LongView';
EXEC sys.sp_addextendedproperty @name=N'MS_DiagramPaneCount',@value=1,@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'VIEW',@level1name=N'PropertyView';
EXEC sys.sp_addextendedproperty @name=N'int-min',@value=@minimum,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
EXEC sys.sp_addextendedproperty @name=N'int-max',@value=@maximum,@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'VIEW',@level1name=N'LongView';
EXEC sys.sp_addextendedproperty @name=N'null',@value=NULL,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
SET @value=@short COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'short-declared-long',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
SET @value=@empty COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'empty-declared',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
SET @value=N'' COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'empty-literal',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
SET @value=CONVERT(nvarchar(300),N'中文 '' ] '+CONVERT(nvarchar(2),0x3DD800DE)+NCHAR(13)+NCHAR(10)+N'GO'+NCHAR(13)+NCHAR(10)+N':r ignored $(name) tail  ') COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
EXEC sys.sp_addextendedproperty @name=N'Property',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
EXEC sys.sp_addextendedproperty @name=N'MS_Description',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property',@level2type=N'COLUMN',@level2name=N'note欄';
EXEC sys.sp_addextendedproperty @name=N'column '' ] custom',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property',@level2type=N'COLUMN',@level2name=N'note欄';
EXEC sys.sp_addextendedproperty @name=N'view-column',@value=@value,@level0type=N'SCHEMA',@level0name=N'dbo',@level1type=N'VIEW',@level1name=N'PropertyView',@level2type=N'COLUMN',@level2name=N'note欄';
SET @value=CONVERT(nvarchar(30),N'drift-original') COLLATE Chinese_Taiwan_Stroke_CI_AS;
EXEC sys.sp_addextendedproperty @name=N'drift',@value=@value,@level0type=N'SCHEMA',@level0name=N'報表',@level1type=N'TABLE',@level1name=N'Property';
