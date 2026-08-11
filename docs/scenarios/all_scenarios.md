*[Scenarios](https://miro.com/app/board/uXjVItVgG_g=/?moveToWidget=3458764680064854482&cot=14)*

| No\. | Method | Data | From System/s | To System/s |
| --- | --- | --- | --- | --- |
| 1 | Publish | As\-Designed/As\-Built Engineering Network/Segment/Tag | ENG | REG\-LOCATION |
| 2 | Publish | As\-Designed/As\-Built Engineering Network/Segment/Tag | REG\-LOCATION | O&amp;M |
| 3 | Publish | As\-Maintained Engineering Network/Segment/Tag | O&amp;M | O&amp;M |
| 4 | Publish | As\-Built Engineering Asset | CONSTRUCT | REG\-ASSET |
| 5 | Publish | As\-Built Engineering Asset | REG\-ASSET | O&amp;M |
| 6 | Publish | As\-Maintained Engineering Asset | O&amp;M | O&amp;M |
| 7 | Pull | OEM Model | MATERIALS, PDM | REG\-MODEL |
| 8 | Pull | OEM Model | REG\-MODEL | O&amp;M |
| 9 | Publish | OEM Model | O&amp;M | O&amp;M |
| 10 | Push | Intelligent Device Removal/Installation | CONTROL | MMS |
| 11 | Publish | Asset Removal/Installation | MMS | O&amp;M |
| 12 | Pull | Usage Readings | HIST | MMS, ORM |
| 13 | Publish | Usage Readings | HIST | MMS, ORM |
| 14 | Push | CBO/CBM Advisories | ORM | MMS, CONTROL |
| 15 | Pull | Work Status/Work History | MMS, CONTROL | O&amp;M |
| 16 | Publish | Work Status/Work History | MMS, CONTROL | O&amp;M |
| 17 | Pull | Maintenance KPIs | MMS | ERP, ERM, PORT, MES, OPM |
| 18 | Publish | Maintenance KPIs | MMS | ERP, ERM, PORT, MES, OPM |
| 19 | Pull | Performance KPIs | ORM | ERP, ERM, PORT, OPM |
| 20 | Publish | Performance KPIs | ORM | ERP, ERM, PORT, OPM |
| 21 | Pull | Significant ORM Events | ORM | ERP, ERM, PORT, OPM |
| 22 | Publish | Significant ORM Events | ORM | ERP, ERM, PORT, OPM |
| 23 | Pull | OPM KPIs | OPM | ERP, ERM, PORT, MES |
| 24 | Publish | OPM KPIs | OPM | ERP, ERM, PORT, MES |
| 25 | Publish | Product/Part Engineering Change Advisories | OEM PDM | REG\-MODEL |
| 26 | Publish | Product/Part Engineering Change Advisories | REG\-MODEL | O&amp;M |
| 27 | Publish | Plant/Process Change Advisories | ENG | REG\-LOCATION |
| 28 | Publish | Plant/Process Change Advisories | REG\-LOCATION | O&amp;M |
| 29 | Publish | Current Operational Data and State Events | CONTROL | O&amp;M |
| 30 | Publish | Current Condition Data and State Events | CMS | O&amp;M |
| 31 | Pull | Historical Operational Data and State Events | CONTROL | O&amp;M |
| 32 | Pull | Historical Condition Data and State Events | CMS | O&amp;M |
| 33 | Pull | Asset Removal/Installation | REG\-ASSET | O&amp;M |
| 34 | Pull | Reference Data | External RDL | Enterprise RDL |
| 35 | Pull | Reference Data | Enterprise RDL | O&amp;M |