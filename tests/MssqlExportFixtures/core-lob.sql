-- Required Core-profile LOB phase, same explicitly owned source database.
-- Small reusable chunks; no whole-LOB client allocation and no multiGB value.
SET NOCOUNT ON;
INSERT dbo.MultiLob(tag,b,n,v,wkt) VALUES (4,0x,N'','',N'LINESTRING(');
DECLARE @i int=0,@b varbinary(max)=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'Z'),65536)),
 @n nvarchar(max)=REPLICATE(CONVERT(nvarchar(max),N'中'),8192),
 @v varbinary(max)=0xA4A4002020;
WHILE @i<1024 BEGIN
 UPDATE dbo.MultiLob SET b.WRITE(@b,NULL,0) WHERE tag=4;
 IF @i<1025 UPDATE dbo.MultiLob SET n.WRITE(@n,NULL,0) WHERE tag=4;
 UPDATE dbo.MultiLob SET v.WRITE(@v,NULL,0),wkt.WRITE(REPLICATE(N'0 0,',256),NULL,0) WHERE tag=4;
 SET @i+=1;
END;
-- 1024 * 16384 = exactly 16 MiB: one more chunk crosses the acceptance threshold.
UPDATE dbo.MultiLob SET n.WRITE(@n,NULL,0),wkt.WRITE(N'1 1)',NULL,0) WHERE tag=4;
SELECT tag,DATALENGTH(b) binary_bytes,DATALENGTH(n) unicode_bytes FROM dbo.MultiLob WHERE tag=4;
-- Expected b=67108864, n=16793600.

-- Large legacy values exercise the same raw staging with a different table shape.
DECLARE @legacy TABLE(v varchar(max) COLLATE Chinese_Taiwan_Stroke_CI_AS);
INSERT @legacy VALUES('');
DECLARE @legacyCount int=0;
WHILE @legacyCount<70000 BEGIN UPDATE @legacy SET v.WRITE(0xA4A4002020,NULL,0); SET @legacyCount+=1; END;
INSERT dbo.MultiLob(tag,t,nt,i) SELECT 5,v,REPLICATE(CONVERT(nvarchar(max),0x3DD800DE0000200000D8),100000),CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'z'),1048576)) FROM @legacy;
