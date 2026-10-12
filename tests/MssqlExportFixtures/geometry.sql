-- Native Geometry fixtures; no client geometry library or normalization.
CREATE TABLE dbo.GeometryCases(id int NOT NULL PRIMARY KEY,label nvarchar(40),g geometry NULL,h geometry NULL,b varbinary(max));
CREATE TABLE dbo.GeometryLarge(id int NOT NULL PRIMARY KEY,g geometry NOT NULL);
CREATE TABLE dbo.GeometryGuard(id int NOT NULL,g geometry NULL);
INSERT dbo.GeometryCases VALUES
(1,N'null and empty',NULL,geometry::STGeomFromText('GEOMETRYCOLLECTION EMPTY',0),0x),
(2,N'point SRID zero',geometry::Point(1,2,0),NULL,0x00FF),
(3,N'point ZM',geometry::STGeomFromText('POINT(1 2 3 4)',3826),geometry::Point(120,24,4326),0x010203),
(4,N'polygon',geometry::STGeomFromText('POLYGON((0 0,0 10,10 10,10 0,0 0))',4326),geometry::STGeomFromText('POINT EMPTY',3826),NULL),
(5,N'collection',geometry::STGeomFromText('GEOMETRYCOLLECTION(POINT(3 4),LINESTRING(0 0,2 2))',0),geometry::Point(5,6,0),0x7F),
(6,N'circularstring',geometry::STGeomFromText('CIRCULARSTRING(0 0,1 1,2 0)',0),NULL,0x),
(7,N'compoundcurve',geometry::STGeomFromText('COMPOUNDCURVE(CIRCULARSTRING(0 0,1 1,2 0),(2 0,3 0))',4326),geometry::STGeomFromText('CURVEPOLYGON(CIRCULARSTRING(0 0,1 1,2 0,1 -1,0 0))',0),0x42),
(8,N'invalid self intersection',geometry::STGeomFromText('POLYGON((0 0,2 2,0 2,2 0,0 0))',0),NULL,0xFF);
