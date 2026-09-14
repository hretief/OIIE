<?xml version="1.0" encoding="UTF-8"?>
<!--
  MmsEngine / SyncSegments — the leg that justifies the WritePlan.

  Five dependent steps: resolve table, resolve owner, upsert, capture the
  IDENTITY-assigned key, register it back. Written as C# this is a bespoke
  orchestrator per participant per noun, and each one is a place to get the
  retry semantics wrong.
-->
<xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:ccom="http://www.mimosa.org/ccom4"
                exclude-result-prefixes="ccom">

  <xsl:output method="xml" indent="yes"/>

  <xsl:param name="targetTable" as="xs:string"/>   <!-- resolved from CIR -->
  <xsl:param name="ownerId"     as="xs:string"/>   <!-- resolved from CIR -->
  <xsl:param name="asOfUtc"     as="xs:string"/>

  <xsl:template match="/ccom:SyncSegments">
    <WritePlan participant="mms" noun="SyncSegments" bodId="{//ccom:BODID}">
      <xsl:apply-templates select="//ccom:Segment"/>
    </WritePlan>
  </xsl:template>

  <xsl:template match="ccom:Segment">
    <Item sourceRef="{ccom:UUID}">

      <!-- resource is a $ref, not a literal: the table comes from CIR. -->
      <Op id="unit" resource="$targetTable" mode="upsert" match="EXT_ASSET_ID">

        <Field name="EXT_ASSET_ID" onUpdate="never">
          <xsl:value-of select="ccom:UUID"/>
        </Field>

        <Field name="LIGHT_UNIT_NAME" onUpdate="set">
          <xsl:value-of select="ccom:ShortName"/>
        </Field>

        <Field name="OWNER_ID" onUpdate="set" ref="$ownerId"/>

        <Field name="DATE_UPDATE" onUpdate="set">
          <xsl:value-of select="$asOfUtc"/>
        </Field>

        <!--
          LIGHT_SYSTEM_ID is DELIBERATELY ABSENT (DR-032).

          It is nullable and describes how MMS groups its own assets — MMS's
          internal organisation, not a fact engineering asserts. Omitting it is
          not the same as writing null: a plan that wrote null would erase
          whatever MMS users had chosen, on every republication.
        -->

        <!-- IDENTITY-assigned by MMS; known only from the response. -->
        <Returns name="LIGHT_UNIT_ID" as="nativeKey"/>
      </Op>

      <!-- Until CIR holds this key, MMS has a row no other participant can name.
           ASSET, not RDL-CLASS — that category holds classes. -->
      <Register cirId="{ccom:UUID}"
                category="ASSET"
                idInSource="unit.nativeKey"
                merge="adopt-existing"/>
    </Item>
  </xsl:template>
</xsl:stylesheet>
