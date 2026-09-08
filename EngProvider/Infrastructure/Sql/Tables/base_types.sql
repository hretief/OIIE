/*
Description
   Used to define basic meta data about entities.
   
Specification
   TODO
*/
BEGIN
   IF dbo.ebfs_exists( 'table', 'base_types', NULL ) = 0
   BEGIN
      EXEC sp_executesql N'
      CREATE TABLE base_types 
      (
         object_type     INT             NOT NULL
            CONSTRAINT base_types_c1 CHECK ( object_type >= 1 ),
         name            NVARCHAR(30)    NOT NULL,
         description     NVARCHAR(255)   NOT NULL,
         table_name      NVARCHAR(30)    NOT NULL,
         key_column      NVARCHAR(30)    NOT NULL,
         table_partition NVARCHAR(255)   NULL,
         scope_type      INT             NOT NULL -- 1 : NotApplicable
                                               -- 2 : GlobalOnly
                                               -- 3 : Any
            CONSTRAINT base_types_c2 CHECK ( scope_type IN ( 1, 2, 3 ) ),
         perm_mask      INT              NOT NULL
            CONSTRAINT base_types_c3 CHECK ( perm_mask >= 0 ),
         lock_type      INT              NOT NULL -- 0 : NotApplicable
                                                  -- 1 : MayAcquire
                                                  -- 2 : MustAcquire
                                                  -- 4 : AutoAcquire
            CONSTRAINT base_types_c4 CHECK ( lock_type >= 0 ),
            CONSTRAINT base_types_pk PRIMARY KEY ( object_type ),
            CONSTRAINT base_types_ak1 unique ( name ),
        is_sync_tracked CHAR(1)         NOT NULL -- Y
                                                  -- N
            CONSTRAINT base_types_c5 CHECK (is_sync_tracked IN ( ''N'', ''Y'' ) ),
         has_guid        CHAR(1)         NOT NULL -- Y
                                                  -- N
            CONSTRAINT base_types_c6 CHECK (has_guid IN ( ''N'', ''Y'' ) )

      )'
   END
END
GO
