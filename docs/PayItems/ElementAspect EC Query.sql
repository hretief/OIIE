SELECT
  ECInstanceId AS AspectId,
  ec_classname(ECClassId, 's:c') AS AspectClass,
  Element.Id AS ElementId,

  SPECBOOK_FILTER AS SpecbookFilter,
  REFITEM_SEARCH AS RefitemSearch,

  PayItem.REFITEM_NM    AS PayItem_RefitemNm,
  PayItem.SpecBook      AS PayItem_SpecBook,
  PayItem.[Class]       AS PayItem_Class,
  PayItem.[Type]        AS PayItem_Type,
  PayItem.Item          AS PayItem_Item,
  PayItem.[Description] AS PayItem_Description,
  PayItem.Unit          AS PayItem_Unit,
  PayItem.UnitPlan      AS PayItem_UnitPlan,
  PayItem.IsPlanQty     AS PayItem_IsPlanQty,

  Funding.[Code]               AS Funding_Code,
  Funding.Percentage           AS Funding_Percentage,
  Funding.FederalParticipation AS Funding_FederalParticipation,
  Funding.Name                 AS Funding_Name,
  Funding.Payer                AS Funding_Payer,

  Cost.EngEstPrice  AS Cost_EngEstPrice,
  Cost.EstimateType AS Cost_EstimateType,
  Cost.Factor       AS Cost_Factor,
  Cost.ItemCost     AS Cost_ItemCost,
  Cost.[Override]   AS Cost_Override,
  Cost.UnitPrice    AS Cost_UnitPrice,

  [Time].EngEstUnitPerDay AS Time_EngEstUnitPerDay,
  [Time].ItemTime         AS Time_ItemTime,
  [Time].UnitFactor       AS Time_UnitFactor,
  [Time].UnitOverride     AS Time_UnitOverride,
  [Time].UnitPerDay       AS Time_UnitPerDay,
  [Time].WorkforceFactor  AS Time_WorkforceFactor,

  ConstructionPhase.ActivityName AS ConstructionPhase_ActivityName,
  ConstructionPhase.Class        AS ConstructionPhase_Class,
  ConstructionPhase.[Group]      AS ConstructionPhase_Group,
  ConstructionPhase.Sequence     AS ConstructionPhase_Sequence,
  ConstructionPhase.Stage        AS ConstructionPhase_Stage,
  ConstructionPhase.YR           AS ConstructionPhase_YR,

  PlanPrep.Label1    AS PlanPrep_Label1,
  PlanPrep.Label2    AS PlanPrep_Label2,
  PlanPrep.LabelNote AS PlanPrep_LabelNote,

  Tabulation.Category                     AS Tabulation_Category,
  Tabulation.Class                        AS Tabulation_Class,
  Tabulation.Code                         AS Tabulation_Code,
  Tabulation.DOT_DigitalAssetMetadata_ID_ AS Tabulation_DOT_DigitalAssetMetadata_ID_,
  Tabulation.[Group]                      AS Tabulation_Group,
  Tabulation.SheetNum                     AS Tabulation_SheetNum,
  Tabulation.Title                        AS Tabulation_Title,

  Asset.AssetID     AS Asset_AssetID,
  Asset.[Index]     AS Asset_Index,
  Asset.Instance    AS Asset_Instance,
  Asset.IsTracked   AS Asset_IsTracked,
  Asset.PlanDecoder AS Asset_PlanDecoder,

  QAQC.ElementDescription AS QAQC_ElementDescription,
  QAQC.IsCheckCost        AS QAQC_IsCheckCost,
  QAQC.IsCheckQty         AS QAQC_IsCheckQty,
  QAQC.IsCommonItem       AS QAQC_IsCommonItem,
  QAQC.IsMajorCost        AS QAQC_IsMajorCost,
  QAQC.IsMajorItemLookup  AS QAQC_IsMajorItemLookup,
  QAQC.IsMajorTime        AS QAQC_IsMajorTime,
  QAQC.IsSiblingRequired  AS QAQC_IsSiblingRequired,
  QAQC.MajorCostValue     AS QAQC_MajorCostValue,
  QAQC.MajorTimeValue     AS QAQC_MajorTimeValue,
  QAQC.PastContractCount  AS QAQC_PastContractCount,

  Help.Link AS Help_Link,
  Help.Tip  AS Help_Tip,

  Remarks.Remarks AS Remarks_Remarks,

  Quantity.CompQuantity          AS Quantity_CompQuantity,
  Quantity.Description           AS Quantity_Description,
  Quantity.Factor                AS Quantity_Factor,
  Quantity.FactorItem            AS Quantity_FactorItem,
  Quantity.FactorUser            AS Quantity_FactorUser,
  Quantity.ItemQuantity          AS Quantity_ItemQuantity,
  Quantity.Method                AS Quantity_Method,
  Quantity.[Override]            AS Quantity_Override,
  Quantity.RoundingConservative  AS Quantity_RoundingConservative,
  Quantity.RoundingIncrement     AS Quantity_RoundingIncrement,
  Quantity.RoundingPrecision     AS Quantity_RoundingPrecision

FROM DgnCustomItemTypes_DOT_DigitalAssetMetadata.DOT_PayItem1ElementAspect
WHERE ElementId = 0x700000000ef;