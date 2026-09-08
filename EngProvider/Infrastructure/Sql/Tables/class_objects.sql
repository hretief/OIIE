/*
Description
   Master table for class definition


   acces_modifiers
      A : Abstract [Must inherit]
      S : Sealed [Cannot inherit]
      V : Virtual [May inherit]
*/
BEGIN
   IF dbo.ebfs_exists( 'table', 'class_objects', NULL ) = 0
   BEGIN
      EXEC sp_executesql N'
      CREATE TABLE class_objects
      (
         class_id                INT            NOT NULL,
         group_id                INT            NOT NULL,
         namespace_id            INT            NOT NULL,
         code                    NVARCHAR(255)  NOT NULL,
         name                    NVARCHAR(255)  NOT NULL,
         description             NVARCHAR(2000) NULL,
         parent_class_id         INT            NULL,
         date_obsolete           DATETIME       NULL,
         superceded_by           INT            NULL,
         access_modifiers        CHAR(1)        NOT NULL
            CONSTRAINT class_objects_c4 CHECK ( access_modifiers IN (''S'',''A'',''V'') ),
            CONSTRAINT class_objects_pk PRIMARY KEY ( class_id ),
            CONSTRAINT class_objects_ak1 UNIQUE ( group_id, code, namespace_id )
      )'
   END

   IF dbo.ebfs_exists( 'foreign key', 'class_objects', 'class_groups_fk1' ) = 0
   BEGIN
      EXEC sp_executesql N'
      ALTER TABLE class_objects ADD CONSTRAINT class_groups_fk1 
      FOREIGN KEY ( group_id ) REFERENCES class_groups( group_id )'
   END

   IF dbo.ebfs_exists( 'foreign key', 'class_objects', 'class_objects_fk1' ) = 0
   BEGIN
      EXEC sp_executesql N'
      ALTER TABLE class_objects ADD CONSTRAINT class_objects_fk1 
      FOREIGN KEY ( parent_class_id ) REFERENCES class_objects( class_id )'
   END

   IF dbo.ebfs_exists( 'foreign key', 'class_objects', 'namespaces_fk12' ) = 0
   BEGIN
      EXEC sp_executesql N'
      ALTER TABLE class_objects ADD CONSTRAINT namespaces_fk12 
      FOREIGN KEY ( namespace_id ) REFERENCES namespaces ( namespace_id )'
   END
END
GO 
