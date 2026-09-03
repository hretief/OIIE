# Get iModel

**GET** `https://api.bentley.com/imodels/{id}`

Retrieves the metadata of an iModel.

---

## Authentication

Requires an `Authorization` header containing a valid Bearer token with the `itwin-platform` scope.

For more information on authorization and obtaining an access token, see:

- [OAuth2 Authorization](https://developer.bentley.com/apis/overview/authorization/)

---

## Authorization

The user must have the `imodels_webview` permission assigned at the **iModel** level.

If iModel Role permissions are configured at the iModel level, the user must additionally have at least the `imodels_webview` permission assigned at the **iTwin** level.

If permissions are not configured at the iModel level, the user must have the `imodels_webview` permission assigned at the **iTwin** level.

Alternatively, the user may be an **Organization Administrator** for the Organization that owns the iTwin containing the iModel.

For more information, see:

- [Account Administrator](https://developer.bentley.com/apis/access-control-v2/overview/#accountadministrator)

---

## Rate Limits

All iTwin Platform API operations are subject to rate limits.

For details, see:

- [Rate Limits and Quotas](https://developer.bentley.com/apis/overview/rate-limits/)

---

## Request

### Path Parameters

| Parameter | Required | Description |
|------------|----------|-------------|
| `id` | Yes | iModel identifier |

### Headers

| Header | Required | Description |
|----------|----------|-------------|
| `Authorization` | Yes | OAuth access token with `itwin-platform` scope |
| `Accept` | Yes | Recommended value: `application/vnd.bentley.itwin-platform.v2+json` |

### Example Request

```http
GET https://api.bentley.com/imodels/{id}

Authorization: Bearer <access-token>
Accept: application/vnd.bentley.itwin-platform.v2+json
```

---

## Response

### 200 OK

```json
{
  "iModel": {
    "id": "5e19bee0-3aea-4355-a9f0-c6df9989ee7d",
    "displayName": "Sun City Renewable-energy Plant",
    "dataCenterLocation": "East US",
    "name": "Sun City Renewable-energy Plant",
    "description": "Overall model of wind and solar farms in Sun City",
    "state": "initialized",
    "createdDateTime": "2020-10-20T10:51:33Z",
    "lastChangesetPushDateTime": null,
    "iTwinId": "5e19bee0-3aea-4355-a9f0-c6df9989ee7d",
    "isSecured": false,
    "extent": {
      "southWest": {
        "latitude": 46.1326770283481,
        "longitude": 7.67212000993845
      },
      "northEast": {
        "latitude": 46.3027639547812,
        "longitude": 7.83554164079782
      }
    }
  }
}
```

### Response Fields

| Field | Description |
|---------|-------------|
| `id` | Unique identifier of the iModel |
| `displayName` | Human-readable display name |
| `dataCenterLocation` | Geographic region where the iModel is hosted |
| `name` | Internal iModel name |
| `description` | Description of the iModel |
| `state` | Current lifecycle state of the iModel |
| `createdDateTime` | Date and time the iModel was created |
| `lastChangesetPushDateTime` | Date and time of the last changeset push |
| `iTwinId` | Identifier of the parent iTwin |
| `isSecured` | Indicates whether the iModel is secured |
| `extent` | Geographic bounding box of the iModel |
| `extent.southWest` | Southwest corner coordinates |
| `extent.northEast` | Northeast corner coordinates |

---