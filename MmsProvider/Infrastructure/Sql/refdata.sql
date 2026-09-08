/* ============================================================================
   MMS -- TAMS reference data.

   Derived from docs/DDL/TAMS_REF_DATA.SQL, the customer's harvested lookup
   values. That source script assigns explicit IDs and clears each table with
   DELETE before reinserting, which only works against tables it also created
   without IDENTITY. Here every *_ID is IDENTITY (see schema.sql), so this
   script seeds by NAME instead of by ID: each INSERT is guarded on
   NOT EXISTS (... WHERE <name column> = ...), which is idempotent regardless
   of whatever IDs the identity column happens to allocate, and never touches
   a row a caller has since edited.

   Run AFTER schema.sql. Every table referenced here must already exist.
   ============================================================================ */

/* -- LIGHT_SYSTEM_CLASS_CODE  (9 values) ------------------------------- */
INSERT INTO dbo.LIGHT_SYSTEM_CLASS_CODE (LIGHT_SYSTEM_CLASS_CODE_NAME)
SELECT v.NAME
FROM (VALUES
    (N'Bridge'), (N'Continuous'), (N'Downtown'), (N'Interchange'),
    (N'Intersection'), (N'Other'), (N'Rest Area'), (N'Tunnel'), (N'Undetermined')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.LIGHT_SYSTEM_CLASS_CODE t WHERE t.LIGHT_SYSTEM_CLASS_CODE_NAME = v.NAME);
GO

/* -- SETUP_ASSET_STATUS  (4 values) ------------------------------------ */
INSERT INTO dbo.SETUP_ASSET_STATUS (ASSET_STATUS_NAME)
SELECT v.NAME
FROM (VALUES
    (N'Abandoned'), (N'Active'), (N'Proposed'), (N'Retired')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_ASSET_STATUS t WHERE t.ASSET_STATUS_NAME = v.NAME);
GO

/* -- SETUP_OWNER  (11 values) ------------------------------------------- */
INSERT INTO dbo.SETUP_OWNER (OWNER_NAME)
SELECT v.NAME
FROM (VALUES
    (N'7000 - Metro District'), (N'7200 - Metro Traffic'), (N'8300 - Maintenance'),
    (N'9100 - District 1'), (N'9200 - District 2'), (N'9300 - District 3'),
    (N'9400 - District 4'), (N'9600 - District 6'), (N'9700 - District 7'),
    (N'9800 - District 8'), (N'MnDOT')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_OWNER t WHERE t.OWNER_NAME = v.NAME);
GO

/* -- SETUP_SGL_DIST_PRIORITY  (8 values) -------------------------------- */
INSERT INTO dbo.SETUP_SGL_DIST_PRIORITY (SGL_DIST_PRIORITY_NAME)
SELECT v.NAME
FROM (VALUES
    (N'LIGHTING- USE WORK REQUEST PRI'), (N'NO STATE RESPONSIBILITY'),
    (N'RTMC-REPAIR IIN 5 WORK DAYS'), (N'RTMC-REPAIR MAY EXCEED 5 WKDYS'),
    (N'SIGNALS- AS SCHEDULE PERMITS'), (N'SIGNALS-CRITICAL  INTERSECTION'),
    (N'SIGNALS-REPAIR NEXT WORK DAY'), (N'UNDECIDED, PRIORITY NOT SET')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_DIST_PRIORITY t WHERE t.SGL_DIST_PRIORITY_NAME = v.NAME);
GO

/* -- SETUP_SGL_ELEC_JUR_CODE  (232 values) ------------------------------ */
INSERT INTO dbo.SETUP_SGL_ELEC_JUR_CODE (SGL_ELEC_JUR_CODE_NAME)
SELECT v.NAME
FROM (VALUES
    (N'ADA'), (N'ADRIAN'), (N'AITKIN (County)'), (N'AKELEY'), (N'ALBERT LEA'),
    (N'ALBERTVILLE'), (N'ALEXANDRIA'), (N'ALVARADO'), (N'ANNANDALE'),
    (N'ANOKA (City Of)'), (N'ANOKA (County)'), (N'ARGYLE'), (N'BAGLEY'),
    (N'BARNESVILLE'), (N'BATTLE LAKE'), (N'BAUDETTE'), (N'BAXTER'),
    (N'BAYPORT'), (N'BECKER (City)'), (N'BECKER (County)'), (N'BELTRAMI (County)'),
    (N'BEMIDJI'), (N'BENSON'), (N'BENTON (County)'), (N'BIG STONE'),
    (N'BIGFORK'), (N'BIRD ISLAND'), (N'BISCAY'), (N'BLACKDUCK'),
    (N'BLOOMINGTON'), (N'BLUE EARTH (County)'), (N'BRECKENRIDGE'),
    (N'BROWERVILLE'), (N'BROWN'), (N'BROWNS VALLEY'), (N'BURNSVILLE'),
    (N'CALEDONIA'), (N'CAMBRIDGE'), (N'CANBY'), (N'CANNON FALLS'),
    (N'CARVER (County)'), (N'CASS'), (N'CASS LAKE'), (N'CHAMPLIN'),
    (N'CHASKA'), (N'CHATFIELD'), (N'CHISAGO CITY'), (N'CHISHOLM'),
    (N'CLARKFIELD'), (N'COMFREY'), (N'COON RAPIDS'), (N'COSMOS'),
    (N'COTTONWOOD'), (N'CROOKSTON'), (N'CROSBY'), (N'CROW WING'),
    (N'CROW WING TOWNSHIP'), (N'DAKOTA (County)'), (N'DAYTON'),
    (N'DEER RIVER'), (N'DETROIT LAKES'), (N'DILWORTH'), (N'DNR'),
    (N'DODGE'), (N'DODGE CENTER'), (N'DOUGLAS'), (N'District 1'),
    (N'District 2'), (N'District 3'), (N'District 3 - Baxter'),
    (N'District 3 - St. Cloud'), (N'District 4'), (N'District 6'),
    (N'District 7'), (N'District 8'), (N'EAST GRAND FORKS'), (N'EDEN PRAIRIE'),
    (N'ELBOW LAKE'), (N'ELK RIVER'), (N'ERSKINE'), (N'Electrical Services'),
    (N'FAIRMONT'), (N'FARIBAULT (City)'), (N'FARIBAULT (County)'),
    (N'FERTILE'), (N'FOLEY'), (N'FOREST LAKE'), (N'FORESTON'), (N'FOSSTON'),
    (N'FRAZEE'), (N'FRIDLEY'), (N'GLENCOE'), (N'GLENWOOD'), (N'GOODHUE (County)'),
    (N'GRANITE FALLS'), (N'GRANT (County)'), (N'GROVE CITY'), (N'HALLOCK'),
    (N'HALSTAD'), (N'HAWLEY'), (N'HECTOR'), (N'HENDRUM'), (N'HENNEPIN'),
    (N'HENNING'), (N'HERMAN'), (N'HOUSTON'), (N'HOWARD LAKE'), (N'HUBBARD'),
    (N'HUTCHINSON'), (N'INDEPENDENCE'), (N'IVANHOE'), (N'Ironton'),
    (N'JACKSON (City)'), (N'JACKSON (County)'), (N'JORDAN'), (N'KANDIYOHI'),
    (N'KASSON'), (N'LA CRESCENT'), (N'LAKE BENTON'), (N'LAKE PARK'),
    (N'LANESBORO'), (N'LE SUEUR (County)'), (N'LEROY'), (N'LITCHFIELD'),
    (N'LUVERNE'), (N'MADISON'), (N'MAHNOMEN (City)'), (N'MANKATO'),
    (N'MARSHALL (City)'), (N'MARTIN'), (N'MAYER'), (N'MET COUNCIL'),
    (N'MIDDLE RIVER'), (N'MILLE LACS'), (N'MINNEAPOLIS'), (N'MINNEOTA'),
    (N'MONTEVIDEO'), (N'MONTGOMERY'), (N'MONTICELLO'), (N'MONTROSE'),
    (N'MOORHEAD'), (N'MORA'), (N'MORGAN'), (N'MORRIS'), (N'MORTON'),
    (N'MOUNTAIN LAKE'), (N'MOWER'), (N'Maintenance'), (N'Metro District'),
    (N'NEW LONDON'), (N'NICOLLET (City)'), (N'NICOLLET (County)'), (N'NISSWA'),
    (N'NOBLES'), (N'NORTH BRANCH'), (N'NORTH MANKATO'), (N'Not Applicable'),
    (N'OLIVIA'), (N'OLMSTED'), (N'ORTONVILLE'), (N'OSAKIS'), (N'OSLO'),
    (N'OTHER'), (N'OTTER TAIL'), (N'OWATONNA'), (N'PARK RAPIDS'),
    (N'PARKERS PRAIRIE'), (N'PAYNESVILLE'), (N'PENNINGTON'), (N'PLUMMER'),
    (N'POLK'), (N'PRESTON'), (N'PRINCETON'), (N'PRIOR LAKE'),
    (N'Power Company'), (N'RAMSEY (County)'), (N'RED LAKE BAND OF CHIPPEWA'),
    (N'RED LAKE FALLS'), (N'RED WING'), (N'REDWOOD FALLS'), (N'REMER'),
    (N'RENVILLE (City)'), (N'RICE (County)'), (N'ROCK'), (N'ROCKVILLE'),
    (N'ROSEAU (City)'), (N'RUSHFORD'), (N'SACRED HEART'), (N'SAVAGE'),
    (N'SCOTT'), (N'SEBEKA'), (N'SHAFER TOWNSHIP'), (N'SHAKOPEE'), (N'SHELLY'),
    (N'SHERBURNE (City)'), (N'SHERBURNE (County)'), (N'SIBLEY'),
    (N'SOUTH ST. PAUL'), (N'SPICER'), (N'SPRING VALLEY'), (N'SPRINGFIELD'),
    (N'ST. CLAIR'), (N'ST. CLOUD'), (N'ST. HILAIRE'), (N'ST. JAMES'),
    (N'ST. LOUIS'), (N'ST. MICHAEL'), (N'ST. PAUL'), (N'ST. PETER'),
    (N'STAPLES'), (N'STARBUCK'), (N'STEARNS'), (N'STEELE'), (N'STEVENS'),
    (N'THIEF RIVER FALLS'), (N'TRUMAN'), (N'TWIN VALLEY'), (N'WAITE PARK'),
    (N'WARREN'), (N'WARROAD'), (N'WASECA (City)'), (N'WASECA (County)'),
    (N'WASHINGTON'), (N'WATONWAN'), (N'WEST CONCORD'), (N'WHEATON'),
    (N'WINDOM'), (N'WINONA (County)'), (N'WOLVERTON'), (N'WORTHINGTON'),
    (N'WRIGHT'), (N'WYOMING')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_ELEC_JUR_CODE t WHERE t.SGL_ELEC_JUR_CODE_NAME = v.NAME);
GO

/* -- SETUP_SGL_ESS_ZONE  (15 values) ------------------------------------ */
INSERT INTO dbo.SETUP_SGL_ESS_ZONE (SGL_ESS_ZONE_NAME)
SELECT v.NAME
FROM (VALUES
    (N'M1'), (N'M2'), (N'M3'), (N'M4'), (N'M5'), (N'M6'), (N'M7'), (N'M8'),
    (N'R1'), (N'R2'), (N'R3'), (N'R4'), (N'R6'), (N'R7'), (N'R8')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_ESS_ZONE t WHERE t.SGL_ESS_ZONE_NAME = v.NAME);
GO

/* -- SETUP_SGL_INSP_COND_RTNG  (8 values) ------------------------------- */
INSERT INTO dbo.SETUP_SGL_INSP_COND_RTNG (SGL_INSP_COND_RTNG_NAME)
SELECT v.NAME
FROM (VALUES
    (N'0-Failed'), (N'4-Poor'), (N'5-Fair'), (N'6-Satisfactory'),
    (N'7-Good'), (N'8-Very Good'), (N'9-Excellent'), (N'N-Unknown')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_INSP_COND_RTNG t WHERE t.SGL_INSP_COND_RTNG_NAME = v.NAME);
GO

/* -- SETUP_SGL_ITS_GEOMSRC  (9 values) ---------------------------------- */
INSERT INTO dbo.SETUP_SGL_ITS_GEOMSRC (SGL_ITS_GEOMSRC_NAME)
SELECT v.NAME
FROM (VALUES
    (N'Asbuilt'), (N'CADD Imported'), (N'GPS Sub Foot'), (N'GPS Sub Meter'),
    (N'GPS greater than Meter'), (N'Hand Drawn'), (N'LiDAR'), (N'Other'), (N'Survey')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_ITS_GEOMSRC t WHERE t.SGL_ITS_GEOMSRC_NAME = v.NAME);
GO

/* -- SETUP_SGL_MAINT_AREA  (15 values) ---------------------------------- */
INSERT INTO dbo.SETUP_SGL_MAINT_AREA (SGL_MAINT_AREA_NAME)
SELECT v.NAME
FROM (VALUES
    (N'1A'), (N'1B'), (N'2A'), (N'2B'), (N'3A'), (N'3B'), (N'4A'), (N'4B'),
    (N'6E'), (N'6W'), (N'7A'), (N'7B'), (N'8B'), (N'ME'), (N'MW')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_MAINT_AREA t WHERE t.SGL_MAINT_AREA_NAME = v.NAME);
GO

/* -- SETUP_SGL_UTIL_POWER_AGENCY  (100 values) -------------------------- */
INSERT INTO dbo.SETUP_SGL_UTIL_POWER_AGENCY (SGL_UTIL_POWER_AGENCY_NAME)
SELECT v.NAME
FROM (VALUES
    (N'Agralite Electric Cooperative'), (N'Aitkin Public Utilities Commission'),
    (N'Alexandria Light and Power'), (N'Alliant Energy'), (N'Anoka Municipal Utility'),
    (N'Arrowhead Electric Cooperative'), (N'Austin Utilities'),
    (N'Beltrami Electric Cooperative'), (N'Benco Electric Cooperative'),
    (N'Brainerd Public Utilities'), (N'Brown County Rural Electric Association'),
    (N'City of Baudette'), (N'City of Blue Earth'), (N'City of Chaska Electric Utility'),
    (N'City of Kasson'), (N'City of Newfolden'), (N'City of North St Paul'),
    (N'City of Randall'), (N'City of Rushford'), (N'City of Staples'),
    (N'Clearwater-Polk Electric Cooperative'), (N'Connexus Energy'),
    (N'Cooperative Light and Power Association'),
    (N'Crow Wing Cooperative Power and Light'), (N'Dakota Electric Association'),
    (N'Detroit Lakes Public Utilities'), (N'East Central Energy'),
    (N'East Grand Forks Water and Light'), (N'Elk River Municipal Utilities'),
    (N'Fairmount Public Utilities Commission'), (N'Federated Rural Electric Association'),
    (N'Freeborn-Mower Cooperative'), (N'Gilbert Water and Light'),
    (N'Goodhue County Cooperative Electric'), (N'Grand Marais Public Utilities'),
    (N'Grand Rapids Public Utilities Commission'), (N'Hibbing Public Utilities Commission'),
    (N'Interstate Power Company'), (N'Itasca-Mantrap Cooperative Electric Association'),
    (N'Janesville Municipal Utilities'), (N'Kandiyohi Power Cooperative'),
    (N'Kenyon Municipal Utilities'), (N'Lake City Public Utilities'),
    (N'Lake Country Power'), (N'Lake Region Electric Cooperative'),
    (N'Lanesboro Public Utility'), (N'Le Sueur Municipal Utilities'),
    (N'Luverne Municipal Electric'), (N'Lyon-Lincoln Electric Cooperative'),
    (N'Marshall Municipal Utilities'), (N'McLeod Cooperative Power Association'),
    (N'Meeker Cooperative Light and Power Association'), (N'MiEnergy Cooperative'),
    (N'Mille Lacs Electric Cooperative'), (N'Minnesota Power'),
    (N'Minnesota Valley Cooperative Light and Power'),
    (N'Minnesota Valley Electric Cooperative'), (N'Moorhead Public Service'),
    (N'Moose Lake Water and Light Commission'), (N'Mora Municipal Utilities'),
    (N'Mountain Iron Water and Light Department'), (N'New Ulm Public Utilities'),
    (N'Nobles Cooperative Electric'), (N'North Itasca Electric Cooperative'),
    (N'North Star Electric Cooperative'), (N'Ortonville Light Department'),
    (N'Otter Tail Power Company'), (N'Owatonna Public Utilities'),
    (N'PKM Electric Cooperative'), (N'People''s Energy Cooperative'),
    (N'Preston Public Utilities'), (N'Princeton Public Utilities'),
    (N'Proctor Public Utilities'), (N'Red Lake Electric Cooperative'),
    (N'Red River Valley Cooperative Power'), (N'Redwood Electric Cooperative'),
    (N'Renville-Sibley Cooperative Power Association'), (N'Rochester Public Utilities'),
    (N'Roseau Electric Cooperative'), (N'Runestone Electric Association'),
    (N'Sauk Center Public Utilities'), (N'Shakopee Public Utilities'),
    (N'Sioux Valley Energy'), (N'South Central Electric Association'),
    (N'Spring Valley Public Utilities Commission'), (N'St Peter Municipal Utilities'),
    (N'Stearns Electric Association'), (N'Steele-Waseca Cooperative Electric'),
    (N'Thief River Falls Municipal Utilities'), (N'Todd-Wadena Electric Cooperative'),
    (N'Traverse Electric Cooperative'), (N'Tri-County Electric Cooperative'),
    (N'Virginia Department of Public Utilities'), (N'Wells Public Utilities Commission'),
    (N'Wild Rice Electric Cooperative'), (N'Willmar Municipal Utilities'),
    (N'Windom Municipal Utilities'), (N'Worthington Public Utilities'),
    (N'Wright-Hennepin Cooperative Electric'), (N'Xcel Energy')
) AS v(NAME)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SETUP_SGL_UTIL_POWER_AGENCY t WHERE t.SGL_UTIL_POWER_AGENCY_NAME = v.NAME);
GO

/* No source values for these lookup tables (created empty by TAMS_REF_DATA.SQL):
   LIGHT_UNIT_CLASS_CODE, SETUP_COUNTY, SETUP_SGL_ELEC_ASM_FOUNDATION,
   SETUP_SGL_ELEC_ASM_LUMHEIGHT, SETUP_SGL_ELEC_ASM_LUM_EXTTYPE,
   SETUP_SGL_ELEC_ASM_MANUF, SETUP_SGL_ELEC_ASM_MASTLENGTH,
   SETUP_SGL_ELEC_ASM_MASTTYPE, SETUP_SGL_ELEC_ASM_SHAFTTYPE,
   SETUP_SGL_ELEC_ASM_TRANSBASE, SETUP_SGL_ELEC_JUR_CODE_TYPE,
   SETUP_SGL_ELEC_LUMINAIRE_TYPE, SETUP_SGL_ELEC_LUM_MFGR,
   SETUP_SGL_ELEC_PART_CAT, SETUP_SGL_ELEC_PART_TYPE,
   SETUP_SGL_ELEC_RESP_TYPE, SETUP_SGL_LIGHT_COMP_TYPE. */
