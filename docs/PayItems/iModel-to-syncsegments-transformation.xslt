<?xml version="1.0" encoding="UTF-8"?>
<!--
  payitem_to_syncsegments.xsl
  ===========================================================================
  Transforms Bentley iTwin PayItem source XML into a schema-valid CCOM
  SyncSegments BOD (the grouped-PropertySet shape).

  INPUTS
    * PRIMARY (context) : payitems XML   (the DOT_PayItem aspect export)
    * document()        : elements XML   (param elementsUri, default src/elements.xml)
                          iTwin XML      (param itwinUri,    default src/itwin.xml)

  OUTPUT
    SyncSegments  (OAGIS envelope + Segments/Segment) with, per matched element:
      RegistrationSite (Project)  <- iTwin + funding/constructionPhase/planPrep/tabulationPlacement
      Segment                     <- element (federationGuid = Segment UUID)
        MaterialItemOnSegment     <- occurrence PropertySet (quantity/qAQC/annotation/extended cost+time)
          MaterialItem            <- priced PropertySet (cost/time factors)
            MaterialMasterItem    <- catalog PropertySet (payItem/help/tabulationCatalog/qAQCThresholds)

  IDENTITY (GUID-stable)
    Segment UUID = element/federationGuid   (real GUID; survives name changes)
    Project UUID = iTwin/id                 (real GUID)
    derived UUIDs = deterministic hex from elementId / RefitemNm  (pattern 8-4-4-4-12)

  DROPPED (Bentley persistence wrapper) : aspectId, aspectClass, asset_PlanDecoder
  DEFERRED (separate SyncAssetSegmentEvents BOD) : asset_AssetID/Index/Instance/IsTracked

  COMPATIBILITY : XSLT 1.0  (runs in Saxon HE/PE/EE and in libxslt/xsltproc).
  ===========================================================================
-->
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns="http://www.mimosa.org/ccom4" xmlns:oa="http://www.openapplications.org/oagis/9"
    exclude-result-prefixes="">

   <xsl:output method="xml" indent="yes" encoding="utf-8"/>
   <xsl:decimal-format name="std" NaN="NaN"/>

   <!-- external inputs -->
   <xsl:param name="elementsUri" select="'element_data.xml'"/>
   <xsl:param name="payitemsUri" select="'payitem_data.xml'"/>
   <xsl:param name="itwinUri" select="'itwins.xml'"/>
   <xsl:param name="bodId" select="'6f2a1c74-9b3e-4d18-9a2f-1c0f5c2d77aa'"/>
   <xsl:param name="senderLogicalID" select="'ENG'"/>


   <xsl:variable name="pays" select="document($payitemsUri)/payitems"/>
   <!--<xsl:variable name="pays" select="document($elementsUri)/payitems"/>-->
   <xsl:variable name="elements" select="document($elementsUri)/elements"/>
   <xsl:variable name="itwin" select="document($itwinUri)/iTwin"/>

   <!-- project base12 = last 12 hex of iTwin id -->
   <xsl:variable name="projBase12" select="substring(translate($itwin/id, '-', ''), 21, 12)"/>

   <!-- ================= root ================= -->
   <xsl:template match="/">
      <SyncSegments releaseID="1.0">
         <oa:ApplicationArea>
            <oa:Sender>
               <oa:LogicalID>
                  <xsl:value-of select="$senderLogicalID"/>
               </oa:LogicalID>
               <oa:ComponentID>Bentley.iModel.PayItemExtractor</oa:ComponentID>
               <oa:TaskID>PayItemSync</oa:TaskID>
            </oa:Sender>
            <oa:CreationDateTime>2026-08-27T11:11:46Z</oa:CreationDateTime>
            <oa:BODID>
               <xsl:value-of select="$bodId"/>
            </oa:BODID>
         </oa:ApplicationArea>
         <DataArea>
            <oa:Sync>
               <oa:ActionCriteria>
                  <oa:ActionExpression actionCode="Replace" expressionLanguage="Xpath"
                            >/SyncSegments/DataArea/Segments</oa:ActionExpression>
               </oa:ActionCriteria>
            </oa:Sync>
            <Segments>
               <!-- one RegistrationSite (project) for the batch -->
               <xsl:call-template name="registrationSite"/>
               <!-- one Segment per source element that has pay items -->
               <xsl:for-each select="$elements/element">
                  <xsl:variable name="eid" select="id"/>
                  <xsl:if test="$pays/payitem[elementId = $eid]">
                     <xsl:call-template name="segment">
                        <xsl:with-param name="element" select="."/>
                     </xsl:call-template>
                  </xsl:if>
               </xsl:for-each>
            </Segments>
         </DataArea>
      </SyncSegments>
   </xsl:template>

   <!-- ================= RegistrationSite (Project) ================= -->
   <xsl:template name="registrationSite">
      <xsl:variable name="pi" select="$pays/payitem[1]"/>
      <RegistrationSite>
         <UUID>
            <xsl:value-of select="$itwin/id"/>
         </UUID>
         <xsl:call-template name="propertySet">
            <xsl:with-param name="setName" select="'DOT_PayItem_Project'"/>
            <xsl:with-param name="base12" select="$projBase12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="groups"
                select="'funding|constructionPhase|planPrep|tabulationPlacement'"/>
         </xsl:call-template>
         <ShortName>
            <xsl:value-of select="$pi/funding_Name"/>
         </ShortName>
         <Description>
            <xsl:value-of select="$itwin/displayName"/>
         </Description>
      </RegistrationSite>
   </xsl:template>

   <!-- ================= Segment ================= -->
   <xsl:template name="segment">
      <xsl:param name="element"/>
      <xsl:variable name="eid" select="$element/id"/>
      <xsl:variable name="segBase12"
          select="substring(concat('0', substring-after($eid, '0x'), '000000000000'), 1, 12)"/>
      <xsl:variable name="pi" select="$pays/payitem[elementId = $eid][1]"/>
      <Segment>
         <UUID>
            <xsl:value-of select="$element/federationGuid"/>
         </UUID>
         <xsl:call-template name="propertySet">
            <xsl:with-param name="setName" select="'DOT_PayItem_Segment'"/>
            <xsl:with-param name="base12" select="$segBase12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="groups" select="'source'"/>
         </xsl:call-template>
         <ShortName>
            <xsl:value-of select="$element/userLabel"/>
         </ShortName>
         <Description>
            <xsl:value-of select="$pi/payItem_Description"/>
         </Description>
         <Type>
            <UUID>
               <xsl:call-template name="mkuuid">
                  <xsl:with-param name="n" select="9001"/>
                  <xsl:with-param name="base12" select="$segBase12"/>
               </xsl:call-template>
            </UUID>
            <ShortName>
               <xsl:value-of
                   select="substring-before(substring-after($element/segmentClass, 'Node__x005C__'), '__x005C__')"
                    />
            </ShortName>
         </Type>
         <!-- one placement per matching pay item -->
         <xsl:for-each select="$pays/payitem[elementId = $eid]">
            <xsl:call-template name="placement">
               <xsl:with-param name="pi" select="."/>
               <xsl:with-param name="segBase12" select="$segBase12"/>
            </xsl:call-template>
         </xsl:for-each>
         <xsl:comment> Tracked Asset + AssetSegmentEvent are NOT children of Segment; they travel in a separate SyncAssetSegmentEvents BOD referencing this Segment UUID. </xsl:comment>
      </Segment>
   </xsl:template>

   <!-- ================= MaterialItemOnSegment (+ MaterialItem + MaterialMasterItem) ================= -->
   <xsl:template name="placement">
      <xsl:param name="pi"/>
      <xsl:param name="segBase12"/>
      <xsl:variable name="digits"
          select="translate($pi/payItem_RefitemNm, translate($pi/payItem_RefitemNm, '0123456789', ''), '')"/>
      <xsl:variable name="catBase12" select="substring(concat($digits, '000000000000'), 1, 12)"/>
      <MaterialItemOnSegment>
         <UUID>
            <xsl:call-template name="mkuuid">
               <xsl:with-param name="n" select="3"/>
               <xsl:with-param name="base12" select="$segBase12"/>
            </xsl:call-template>
         </UUID>
         <xsl:call-template name="propertySet">
            <xsl:with-param name="setName" select="'DOT_PayItem_Occurrence'"/>
            <xsl:with-param name="base12" select="$segBase12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="groups"
                select="'quantity|costExtended|timeExtended|qAQC|annotation'"/>
         </xsl:call-template>
         <MaterialItem>
            <UUID>
               <xsl:call-template name="mkuuid">
                  <xsl:with-param name="n" select="2"/>
                  <xsl:with-param name="base12" select="$catBase12"/>
               </xsl:call-template>
            </UUID>
            <xsl:call-template name="propertySet">
               <xsl:with-param name="setName" select="'DOT_PayItem_Priced'"/>
               <xsl:with-param name="base12" select="$catBase12"/>
               <xsl:with-param name="pi" select="$pi"/>
               <xsl:with-param name="groups" select="'cost|time'"/>
            </xsl:call-template>
            <ShortName>
               Priced item - <xsl:value-of select="$pi/funding_Name"/>
            </ShortName>
            <MaterialMasterItem>
               <UUID>
                  <xsl:call-template name="mkuuid">
                     <xsl:with-param name="n" select="1"/>
                     <xsl:with-param name="base12" select="$catBase12"/>
                  </xsl:call-template>
               </UUID>
               <xsl:call-template name="propertySet">
                  <xsl:with-param name="setName" select="'DOT_PayItem_Catalog'"/>
                  <xsl:with-param name="base12" select="$catBase12"/>
                  <xsl:with-param name="pi" select="$pi"/>
                  <xsl:with-param name="groups"
                      select="'payItem|lookup|help|tabulationCatalog|qAQCThresholds'"/>
               </xsl:call-template>
               <ShortName>
                  <xsl:value-of select="$pi/payItem_Item"/>
               </ShortName>
               <Description>
                  <xsl:value-of select="$pi/payItem_Description"/>
               </Description>
            </MaterialMasterItem>
         </MaterialItem>
         <Segment>
            <UUID>
               <xsl:value-of select="$elements/element[id = $pi/elementId]/federationGuid"/>
            </UUID>
         </Segment>
      </MaterialItemOnSegment>
   </xsl:template>

   <!-- ================= PropertySet dispatcher ================= -->
   <xsl:template name="propertySet">
      <xsl:param name="setName"/>
      <xsl:param name="base12"/>
      <xsl:param name="pi"/>
      <xsl:param name="groups"/>
      <!-- pipe-delimited group ids -->
      <PropertySetForEntity>
         <UUID>
            <xsl:call-template name="mkuuid">
               <xsl:with-param name="n" select="7000"/>
               <xsl:with-param name="base12" select="$base12"/>
            </xsl:call-template>
         </UUID>
         <PropertySet>
            <UUID>
               <xsl:call-template name="mkuuid">
                  <xsl:with-param name="n" select="6000"/>
                  <xsl:with-param name="base12" select="$base12"/>
               </xsl:call-template>
            </UUID>
            <ShortName>
               <xsl:value-of select="$setName"/>
            </ShortName>
            <xsl:call-template name="emitGroups">
               <xsl:with-param name="list" select="concat($groups, '|')"/>
               <xsl:with-param name="base12" select="$base12"/>
               <xsl:with-param name="pi" select="$pi"/>
               <xsl:with-param name="order" select="1"/>
            </xsl:call-template>
         </PropertySet>
      </PropertySetForEntity>
   </xsl:template>

   <!-- iterate the pipe-delimited group list -->
   <xsl:template name="emitGroups">
      <xsl:param name="list"/>
      <xsl:param name="base12"/>
      <xsl:param name="pi"/>
      <xsl:param name="order"/>
      <xsl:if test="$list != ''">
         <xsl:variable name="g" select="substring-before($list, '|')"/>
         <xsl:variable name="rest" select="substring-after($list, '|')"/>
         <xsl:call-template name="emitGroup">
            <xsl:with-param name="gid" select="$g"/>
            <xsl:with-param name="order" select="$order"/>
            <xsl:with-param name="base12" select="$base12"/>
            <xsl:with-param name="pi" select="$pi"/>
         </xsl:call-template>
         <xsl:call-template name="emitGroups">
            <xsl:with-param name="list" select="$rest"/>
            <xsl:with-param name="base12" select="$base12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="order" select="$order + 1"/>
         </xsl:call-template>
      </xsl:if>
   </xsl:template>

   <!-- one PropertyGroup: look up its field token list, emit ShortName/Order, then SetProperties -->
   <xsl:template name="emitGroup">
      <xsl:param name="gid"/>
      <xsl:param name="order"/>
      <xsl:param name="base12"/>
      <xsl:param name="pi"/>

      <!-- (fieldName=ShortName ...) token lists, per group; the field/entity split lives here -->
      <xsl:variable name="tokens">
         <xsl:choose>
            <xsl:when test="$gid = 'payItem'">
               payItem_RefitemNm=RefitemNm
               payItem_SpecBook=SpecBook payItem_Class=Class payItem_Type=Type
               payItem_Item=Item payItem_Description=Description payItem_Unit=Unit
               payItem_UnitPlan=UnitPlan payItem_IsPlanQty=IsPlanQty
            </xsl:when>
            <xsl:when test="$gid = 'lookup'">
               refitemSearch=Search
               specbookFilter=SpecBookFilter
            </xsl:when>
            <xsl:when test="$gid = 'help'">help_Link=Link help_Tip=Tip</xsl:when>
            <xsl:when test="$gid = 'tabulationCatalog'">
               tabulation_Category=Category
               tabulation_Class=Class tabulation_Title=Title
            </xsl:when>
            <xsl:when test="$gid = 'qAQCThresholds'">
               qAQC_MajorCostValue=MajorCostValue
               qAQC_MajorTimeValue=MajorTimeValue
               qAQC_PastContractCount=PastContractCount
            </xsl:when>
            <xsl:when test="$gid = 'cost'">
               cost_EngEstPrice=EngEstPrice
               cost_EstimateType=EstimateType cost_Factor=Factor cost_UnitPrice=UnitPrice
               cost_Override=Override
            </xsl:when>
            <xsl:when test="$gid = 'time'">
               time_EngEstUnitPerDay=EngEstUnitPerDay
               time_UnitFactor=UnitFactor time_UnitPerDay=UnitPerDay
               time_WorkforceFactor=WorkforceFactor
            </xsl:when>
            <xsl:when test="$gid = 'quantity'">
               quantity_CompQuantity=CompQuantity
               quantity_ItemQuantity=ItemQuantity quantity_Method=Method
               quantity_Description=Description quantity_Factor=Factor
               quantity_FactorItem=FactorItem quantity_FactorUser=FactorUser
               quantity_Override=Override quantity_RoundingConservative=RoundingConservative
               quantity_RoundingIncrement=RoundingIncrement
               quantity_RoundingPrecision=RoundingPrecision
            </xsl:when>
            <xsl:when test="$gid = 'costExtended'">cost_ItemCost=ItemCost</xsl:when>
            <xsl:when test="$gid = 'timeExtended'">
               time_ItemTime=ItemTime
               time_UnitOverride=UnitOverride
            </xsl:when>
            <xsl:when test="$gid = 'qAQC'">
               qAQC_IsCheckCost=IsCheckCost
               qAQC_IsCheckQty=IsCheckQty qAQC_IsCommonItem=IsCommonItem
               qAQC_IsMajorCost=IsMajorCost qAQC_IsMajorItemLookup=IsMajorItemLookup
               qAQC_IsMajorTime=IsMajorTime qAQC_IsSiblingRequired=IsSiblingRequired
            </xsl:when>
            <xsl:when test="$gid = 'annotation'">
               qAQC_ElementDescription=ElementDescription
               remarks_Remarks=Remarks
            </xsl:when>
            <xsl:when test="$gid = 'funding'">
               funding_Code=Code funding_Percentage=Percentage
               funding_FederalParticipation=FederalParticipation funding_Name=Name
               funding_Payer=Payer
            </xsl:when>
            <xsl:when test="$gid = 'constructionPhase'"
                    >
               constructionPhase_ActivityName=ActivityName constructionPhase_Class=Class
               constructionPhase_Group=Group constructionPhase_Sequence=Sequence
               constructionPhase_Stage=Stage constructionPhase_YR=YR
            </xsl:when>
            <xsl:when test="$gid = 'planPrep'">
               planPrep_Label1=Label1 planPrep_Label2=Label2
               planPrep_LabelNote=LabelNote
            </xsl:when>
            <xsl:when test="$gid = 'tabulationPlacement'">
               tabulation_Code=Code
               tabulation_DOT_DigitalAssetMetadata_ID_=DOT_ID tabulation_Group=Group
               tabulation_SheetNum=SheetNum
            </xsl:when>
            <xsl:when test="$gid = 'source'">elementId=elementId</xsl:when>
         </xsl:choose>
      </xsl:variable>

      <Group>
         <UUID>
            <xsl:call-template name="mkuuid">
               <xsl:with-param name="n" select="5000 + $order"/>
               <xsl:with-param name="base12" select="$base12"/>
            </xsl:call-template>
         </UUID>
         <ShortName>
            <xsl:value-of select="$gid"/>
         </ShortName>
         <Order>
            <xsl:value-of select="$order"/>
         </Order>
         <xsl:call-template name="emitProps">
            <xsl:with-param name="tokens" select="concat(normalize-space($tokens), ' ')"/>
            <xsl:with-param name="base12" select="$base12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="order" select="$order"/>
            <xsl:with-param name="idx" select="1"/>
         </xsl:call-template>
      </Group>
   </xsl:template>

   <!-- emit SetProperty per token (field=ShortName), typed by value/name -->
   <xsl:template name="emitProps">
      <xsl:param name="tokens"/>
      <xsl:param name="base12"/>
      <xsl:param name="pi"/>
      <xsl:param name="order"/>
      <xsl:param name="idx"/>
      <xsl:if test="normalize-space($tokens) != ''">
         <xsl:variable name="tok" select="substring-before($tokens, ' ')"/>
         <xsl:variable name="rest" select="substring-after($tokens, ' ')"/>
         <xsl:variable name="field" select="substring-before($tok, '=')"/>
         <xsl:variable name="short" select="substring-after($tok, '=')"/>
         <xsl:variable name="val" select="normalize-space($pi/*[local-name() = $field])"/>
         <SetProperty>
            <UUID>
               <xsl:call-template name="mkuuid">
                  <xsl:with-param name="n" select="$order * 100 + $idx"/>
                  <xsl:with-param name="base12" select="$base12"/>
               </xsl:call-template>
            </UUID>
            <ShortName>
               <xsl:value-of select="$short"/>
            </ShortName>
            <xsl:call-template name="valueContent">
               <xsl:with-param name="field" select="$field"/>
               <xsl:with-param name="val" select="$val"/>
               <xsl:with-param name="base12" select="$base12"/>
               <xsl:with-param name="n" select="8000 + $order * 100 + $idx"/>
            </xsl:call-template>
         </SetProperty>
         <xsl:call-template name="emitProps">
            <xsl:with-param name="tokens" select="$rest"/>
            <xsl:with-param name="base12" select="$base12"/>
            <xsl:with-param name="pi" select="$pi"/>
            <xsl:with-param name="order" select="$order"/>
            <xsl:with-param name="idx" select="$idx + 1"/>
         </xsl:call-template>
      </xsl:if>
   </xsl:template>

   <!-- ValueContent typing: Measure (money) | Boolean | Number | Text -->
   <xsl:template name="valueContent">
      <xsl:param name="field"/>
      <xsl:param name="val"/>
      <xsl:param name="base12"/>
      <xsl:param name="n"/>
      <xsl:variable name="lc" select="translate($val, 'TRUE', 'true')"/>
      <ValueContent>
         <xsl:choose>
            <!-- money -> Measure with USD UoM -->
            <xsl:when test="contains($field, 'Price') or contains($field, 'ItemCost')">
               <Measure>
                  <Value>
                     <xsl:choose>
                        <xsl:when test="$val != '' and number($val) = number($val)">
                           <xsl:value-of select="$val"/>
                        </xsl:when>
                        <xsl:otherwise>0</xsl:otherwise>
                     </xsl:choose>
                  </Value>
                  <UnitOfMeasure>
                     <UUID>
                        <xsl:call-template name="mkuuid">
                           <xsl:with-param name="n" select="$n"/>
                           <xsl:with-param name="base12" select="$base12"/>
                        </xsl:call-template>
                     </UUID>
                     <ShortName>USD</ShortName>
                  </UnitOfMeasure>
               </Measure>
            </xsl:when>
            <!-- boolean -->
            <xsl:when test="$val = 'True' or $val = 'False' or $val = 'true' or $val = 'false'">
               <Boolean>
                  <xsl:value-of select="translate($val, 'TF', 'tf')"/>
               </Boolean>
            </xsl:when>
            <!-- numeric -->
            <xsl:when test="$val != '' and number($val) = number($val)">
               <Number>
                  <xsl:value-of select="$val"/>
               </Number>
            </xsl:when>
            <!-- fallback text (incl. 'nan' placeholders) -->
            <xsl:otherwise>
               <Text>
                  <xsl:value-of select="$val"/>
               </Text>
            </xsl:otherwise>
         </xsl:choose>
      </ValueContent>
   </xsl:template>

   <!-- deterministic UUID: 00000000-0000-4000-NNNN-<base12> -->
   <xsl:template name="mkuuid">
      <xsl:param name="n"/>
      <xsl:param name="base12"/>
      <xsl:value-of
          select="concat('00000000-0000-4000-', format-number($n mod 10000, '0000', 'std'), '-', $base12)"
        />
   </xsl:template>

</xsl:stylesheet>