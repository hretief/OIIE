-----------------------------------------------------------------------------------------
-- $Copyright: (c) 2015 Bentley Systems, Incorporated. All rights reserved. $
-----------------------------------------------------------------------------------------

/*
Description
   This procedure deletes a Site.
*/
EXEC ebps_drop 'procedure', 'ccp_del_site'
GO
CREATE PROCEDURE ccp_del_site
(   
   @pguid_site_uuid  UNIQUEIDENTIFIER,
   @pi_called_by     INT
)
AS
DECLARE 
   @li_error     INT,
   @li_serial_id INT,
   @li_item_id   INT,
   @li_scope_id  INT,
   @li_id        INT
BEGIN
   SET NOCOUNT ON;
   -- Assert Mandatory GUID Values
   EXEC @li_error = ccp_assert_isnullorempty_g @pguid_site_uuid, NULL, N'Site UUID'
   IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

   --       Tasks
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 340   -- events
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 340 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ebp_del_task @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --       Events
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 94   -- events
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 94 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ebp_del_event @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --       Measurements
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 360   -- cc_measurements
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 360 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ccp_del_cc_measurement2 @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --       Regions
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 361   -- cc_regions
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 361 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ccp_del_cc_region2 @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --       Locations (missing from core procedure)
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 12   -- Locations
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 12 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ebp_del_location @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --       Requests
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 362   -- cc_requests
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 362 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC
   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      EXEC @li_error = ccp_del_cc_request2 @li_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope


   --       External Objects (missing from core procedure)
   --       remove all external objects to avoid objects_fk14 violation during ebp_del_scope
   DECLARE lcur_del_scope CURSOR LOCAL READ_ONLY FORWARD_ONLY FOR
   SELECT b.object_id FROM objects a WITH (NOLOCK) INNER JOIN objects b WITH (NOLOCK) ON a.object_id = b.scope_id AND b.object_type = 313   -- Locations
   WHERE a.object_type = 227 AND a.guid = @pguid_site_uuid
   AND b.object_id NOT IN ( SELECT object_id FROM templates WITH(NOLOCK) WHERE object_type = 313 ) -- Exclude template objects, deleting templates later will take care of them
   ORDER BY b.object_id DESC

   OPEN lcur_del_scope
   FETCH NEXT FROM lcur_del_scope INTO @li_id
   WHILE (@@FETCH_STATUS = 0)
   BEGIN
      -- NOTE, we have to check if the object does exist because the ebp_del_external_object can cause a cascaded delete 
      IF EXISTS( SELECT 1 FROM external_objects WHERE ext_object_id = @li_id )
      BEGIN
         EXEC @li_error = ebp_del_external_object @li_id, @pi_called_by
         IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure
      END

      FETCH NEXT FROM lcur_del_scope INTO @li_id
   END
   CLOSE lcur_del_scope
   DEALLOCATE lcur_del_scope

   --Remove the serial associated with this site
   SELECT @li_serial_id = serial_id FROM item_serial_nos a WITH (NOLOCK) 
   INNER JOIN objects b WITH (NOLOCK) ON a.serial_id = b.object_id AND b.object_type = 18 WHERE b.guid = @pguid_site_uuid
   IF COALESCE(@li_serial_id,0) > 0
   BEGIN
      -- clear the log
      DELETE tag_serial_log WHERE serial_id = @li_serial_id
      IF @@ERROR <> 0 RETURN 1 -- Failure

      EXEC @li_error = ebp_del_serial_number @li_serial_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure
   END

   --Remove the item associated with this site
   SELECT @li_item_id = a.item_id FROM item_serial_nos a WITH (NOLOCK)
   INNER JOIN objects b WITH (NOLOCK) ON a.item_id = b.object_id AND b.object_type = 18 WHERE b.guid = @pguid_site_uuid;
   IF COALESCE(@li_item_id,0) > 0
   BEGIN
      EXEC @li_error = ebp_del_item @li_item_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure
   END

   -- Remove the scope associated with this site
   SELECT @li_scope_id = a.scope_id FROM scopes a WITH (NOLOCK) 
   INNER JOIN objects b WITH (NOLOCK) ON a.scope_id = b.object_id AND b.object_type = 227 WHERE b.guid = @pguid_site_uuid
   IF COALESCE(@li_scope_id,0) > 0
   BEGIN
      EXEC @li_error = ebp_del_scope @li_scope_id, @pi_called_by
      IF @@ERROR <> 0 OR @li_error <> 0 RETURN 1 -- Failure
   END

   RETURN 0 -- Success
END
GO
