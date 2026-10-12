-- Caller substitutes only an integer @Rows: baseline 1024; expanded 10240.
-- Execute in separate owned empty source DBs, or DROP only this owned table between runs.
-- 128 KiB per row; 128 MiB baseline / 1280 MiB expanded payload; hex >2560 MiB SQL.
SET NOCOUNT ON;
CREATE TABLE dbo.LargeBinary(id int NOT NULL PRIMARY KEY,payload varbinary(max) NOT NULL);
DECLARE @Rows int=1024; -- replace with 10240 for expanded run, record actual parameter
DECLARE @i int=1,@chunk varbinary(max)=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'Z'),65536));
WHILE @i<=@Rows BEGIN
 INSERT dbo.LargeBinary VALUES (@i,0x);
 UPDATE dbo.LargeBinary SET payload.WRITE(@chunk,NULL,0) WHERE id=@i;
 UPDATE dbo.LargeBinary SET payload.WRITE(@chunk,NULL,0) WHERE id=@i;
 SET @i+=1;
END;
SELECT COUNT_BIG(*) rows,SUM(CONVERT(bigint,DATALENGTH(payload))) source_payload_bytes FROM dbo.LargeBinary;
