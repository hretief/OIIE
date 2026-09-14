<?xml version="1.0" encoding="UTF-8"?>
<!--
  MmsEngine / SyncSites (oa:Sync)

  Authored by whoever owns the MMS data model. Emits a WritePlan, never a
  native payload: grouping the mapped fields under <Op resource="..."> IS the
  API dispatch decision, so the author is never asked "which endpoint?".
-->
<xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:ccom="http://www.mimosa.org/ccom4"
                exclude-result-prefixes="ccom">

  <xsl:output method="xml" indent="yes"/>

  <!-- Supplied by the engine. A stylesheet has no other way to receive data. -->
  <xsl:param name="asOfUtc" as="xs:string"/>

  <xsl:template match="/ccom:SyncSites">
    <WritePlan participant="mms" noun="SyncSites" bodId="{//ccom:BODID}">
      <xsl:apply-templates select="//ccom:Site"/>
    </WritePlan>
  </xsl:template>

  <xsl:template match="ccom:Site">
    <Item sourceRef="{ccom:UUID}">
      <Op id="system"
          resource="LIGHT_SYSTEM_INVENTORY"
          mode="upsert"
          match="EXT_ASSET_ID">

        <!-- Match key. never = insert only; the executor always sends it for
             matching but never as an update. -->
        <Field name="EXT_ASSET_ID" onUpdate="never">
          <xsl:value-of select="ccom:UUID"/>
        </Field>

        <Field name="LIGHT_SYSTEM_NAME" onUpdate="set">
          <xsl:value-of select="ccom:ShortName"/>
        </Field>

        <!-- setIfAbsent, NOT set: MMS reclassifies this itself, and a
             republished site must not reset the classification. -->
        <Field name="CLASSIFICATION" onUpdate="setIfAbsent">Undetermined</Field>

        <Field name="DATE_UPDATE" onUpdate="set">
          <xsl:value-of select="$asOfUtc"/>
        </Field>
      </Op>
    </Item>
  </xsl:template>
</xsl:stylesheet>
