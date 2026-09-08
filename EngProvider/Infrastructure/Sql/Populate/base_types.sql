-----------------------------------------------------------------------------------------
-- $Copyright: (c) Bentley Systems, Incorporated. All rights reserved. $
-----------------------------------------------------------------------------------------

/*
Description
   Populates the base_type table.

Note
   The perm_mask is an 'OR' of
       1 : View
       2 : Modify
       4 : Approve
       8 : Can Use
      16 : Delete
      So for example: 31 for all, 23 for all except 'CanUse' and 19 for all except 'Approve' and CanUse'

      For objects that are G0A use mask '0'

      E.g:
      -- Show all GB permissions:
      SELECT * FROM base_types WHERE perm_mask > 0

      -- Show all objects with 'CanUse'
      SELECT * FROM base_types WHERE perm_mask&8 > 0

      SELECT 19|4

   Scope Type:
      1 : Not Applicable
      2 : Global only
      3 : Any

   The lock_type can be:
      0 : NotApplicable
      1 : MayAcquire
      2 : MustAcquire (on update or delete only)
      4 : AutoAcquire (on update or delete only)

Note:
   Following object types are introduced LinearLocation, LinearReferenceMethod, LinearElement, LinearRange, LinearElementType for Exor and object entries are added into objects table.
   corresponding tables reside in EXOR DB at present. The table_partition ise set as '0=1'; this creates a condition that excludes any entries made in the objects table 
*/
EXEC ebps_drop 'procedure', 'ebps_populate_base_types'
GO
CREATE PROCEDURE ebps_populate_base_types
(
   @pi_object_type     INT,
   @ps_name            NVARCHAR(30),
   @ps_description     NVARCHAR(255),
   @ps_table_name      NVARCHAR(30),
   @ps_key_column      NVARCHAR(30),
   @ps_table_partition NVARCHAR(255),
   @pi_scope_type      INT,
   @pi_perm_mask       INT,
   @pi_lock_type       INT,
   @ps_is_sync_tracked CHAR(1),
   @ps_has_guid        CHAR(1)
)
AS
DECLARE
   @ls_table_partition NVARCHAR(255),
   @li_result          INT
BEGIN
   SET NOCOUNT ON;

   SET @ls_table_partition = @ps_table_partition;
   IF @ls_table_partition = N'' SET @ls_table_partition = NULL

   -- has_guid is purposely set to N for new rows, if it should be enabled ebp_chg_base_type is used further down to not only set the flag put generate guids for existing objects that does not yet have a guid
   INSERT INTO base_types( object_type, name, description, table_name, key_column, table_partition, scope_type, perm_mask, lock_type, is_sync_tracked, has_guid )
   SELECT @pi_object_type, @ps_name, @ps_description, @ps_table_name, @ps_key_column, @ls_table_partition, @pi_scope_type, @pi_perm_mask, @pi_lock_type, @ps_is_sync_tracked, N'N'
   WHERE NOT EXISTS( SELECT 1 FROM base_types WHERE object_type = @pi_object_type )
   IF @@ERROR <> 0 RETURN 1 -- Failure

   UPDATE base_types
   SET name = @ps_name, description = @ps_description, table_name = @ps_table_name, key_column = @ps_key_column,
       table_partition = @ls_table_partition, scope_type = @pi_scope_type, perm_mask = @pi_perm_mask, lock_type = @pi_lock_type
   WHERE object_type = @pi_object_type
   IF @@ERROR <> 0 RETURN 1 -- Failure

   -- If guids are enabled for this object, this procedure will set the flag in the table as well as generate guids for existing instances
   -- Once guids are enabled for a type they can't be turned off so only do this if has_guid=Y
   IF @ps_has_guid = N'Y'
   BEGIN
      EXEC @li_result = ebp_chg_base_type @pi_object_type, @ps_has_guid, 1
      IF @li_result <> 0 OR @@ERROR <> 0 RETURN 1 -- Failure
   END

   RETURN 0 -- Success
END
GO

BEGIN
   EXEC ebps_populate_base_types 181, N'AttributeDef', N'Attribute Definition', N'characteristics', N'char_id', N'', 3, 0, 0, 'N', 'N'
   EXEC ebps_populate_base_types 207, N'AttributeGroup', N'Attribute Group', N'attribute_groups', N'group_id', N'', 3, 0, 0, 'N', 'N'
   EXEC ebps_populate_base_types 6, N'ChangeRequest', N'Change Request', N'change_requests', N'co_id', N'', 3, 23, 1, 'N', 'N'
   EXEC ebps_populate_base_types 185, N'Class', N'Class', N'class_objects', N'class_id', N'', 3, 0, 0, 'N', 'N'
   EXEC ebps_populate_base_types 184, N'ClassGroup', N'Class Group', N'class_groups', N'group_id', N'', 2, 0, 0, 'N', 'N'

END
GO
EXEC ebps_drop 'procedure', 'ebps_populate_base_types'
GO
