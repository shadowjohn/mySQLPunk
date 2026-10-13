CREATE TABLE dbo.Parent(id int NOT NULL PRIMARY KEY);
CREATE TABLE dbo.Child(id int NOT NULL PRIMARY KEY,parent int NOT NULL,CONSTRAINT FK_Snapshot_Child_Parent FOREIGN KEY(parent) REFERENCES dbo.Parent(id));
INSERT dbo.Parent VALUES(1);
INSERT dbo.Child VALUES(1,1);
