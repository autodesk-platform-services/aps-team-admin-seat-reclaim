# aps-team-admin-api

![platforms](https://img.shields.io/badge/platform-windows%20%7C%20osx%20%7C%20linux-lightgray.svg)
[![.net](https://img.shields.io/badge/net-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![license](https://img.shields.io/:license-mit-blue.svg)](https://opensource.org/licenses/MIT)

Sample .NET application that uses **3-legged OAuth with PKCE**, the **Business Success Plan Reporting API**
(Insights) and the **Team Admin API** to reclaim licenses from inactive users.

1. The user signs in with Autodesk (PKCE plus the client secret of a Traditional Web App).
2. The app lists the teams the user administers (Team Admin `GET /teams`) and checks which of them have the
   Business Success Plan Reporting API benefit (Insights `GET /contexts`, `ReportingAPIAccess`).
3. For a selected team, the user chooses **inactive for the past N days** and a usage source:
   - **Business Success Plan Reporting API**: the app runs a Usage Query
     (`POST /insights/v1/usage-queries`) for the latest usage per user and product, as in
     [aps-business-success-plan-report](https://github.com/autodesk-platform-services/aps-business-success-plan-report).
   - **Upload a report**: a CSV, XLSX or ZIP report (see [Accepted reports](#accepted-reports)).
4. The app matches usage with the team's product assignments and shows every **user + product** without usage in
   the window, including users who never used the product.
5. The user ticks the licenses to reclaim, and the app removes them with
   `DELETE /team-admin/v1/teams/{teamId}/assignments?userId=&productId=`.

The project structure follows [aps-hubs-browser-dotnet](https://github.com/autodesk-platform-services/aps-hubs-browser-dotnet):
a singleton `APS` partial class in `Models/` (one file per API), thin controllers in `Controllers/`, and a static
frontend in `wwwroot/`.

## Development

### Prerequisites

- An [APS application](https://aps.autodesk.com/en/docs/oauth/v2/tutorials/create-app):
  - Type **Traditional Web App**. The Team Admin API [supports only this app type](https://aps.autodesk.com/en/docs/team_admin/v1/developers_guide/about_beta/#before-you-begin),
    so public clients (Desktop, Mobile, Single-Page App) can't be used and the client secret is required.
  - Callback URL `http://localhost:8080/api/auth/callback`.
  - Access to the **Team Admin API** and the **Business Success Plan Reporting (Insights) API**.
- The signed-in user must administer the team in Autodesk Account. To use the Reporting API source, the team must also
  have Business Success Plan reporting with API access. Without it, upload a usage report instead.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Setup and run

1. Clone this repository and open the folder.
2. Create `appsettings.Development.json` (it is ignored by git):

    ```json
    {
      "APS_CLIENT_ID": "<client-id>",
      "APS_CLIENT_SECRET": "<client-secret>",
      "APS_CALLBACK_URL": "http://localhost:8080/api/auth/callback"
    }
    ```

    You can also set these keys as environment variables.

    | Key | Required | Description |
    | --- | --- | --- |
    | `APS_CLIENT_ID` | yes | Client ID of the APS application |
    | `APS_CALLBACK_URL` | yes | Must match the callback URL registered in the APS application |
    | `APS_CLIENT_SECRET` | yes | Client secret of the Traditional Web App |
    | `APS_BASE_URL` | no | Defaults to `https://developer.api.autodesk.com`. Use `https://developer-stg.api.autodesk.com` for staging |

3. Run `dotnet run` (or press F5 in VS Code or Visual Studio) and open http://localhost:8080.

Run the unit tests with `dotnet test`.

## How it works

### Authentication (PKCE)

- `GET /api/auth/login`:
  - Creates a code verifier, its S256 challenge and a `state` value.
  - Stores the verifier and state in a short-lived, encrypted, HttpOnly cookie.
  - Redirects to `/authentication/v2/authorize`. The URL is built by hand because the SDK's `Scopes` enum has no `tmadmapi:*` values.
- `GET /api/auth/callback`:
  - Checks `state`.
  - Exchanges the code and the verifier, refreshes tokens and reads the user profile with the official [`Autodesk.Authentication`](https://www.nuget.org/packages/Autodesk.Authentication) SDK.
  - Stores the tokens in encrypted HttpOnly cookies. Tokens refresh automatically when they expire.
- Requested scopes are `openid profapi:core-std-profile:read tmadmapi:team:read tmadmapi:team-users:read tmadmapi:team-products:read tmadmapi:team-products:write data:read data:write`. `openid` and `profapi:core-std-profile:read` are needed for `/userinfo` (User Profile API), and `data:write` is needed because usage queries are submitted with POST.

### Server endpoints

| Endpoint | Description |
| --- | --- |
| `GET /api/auth/login`, `/callback`, `/logout`, `/profile` | Sign-in flow and user info |
| `GET /api/teams` | Teams you administer, each with a `reportingApiAccess` flag |
| `POST /api/teams/{teamId}/inactive-users` | `{ days, productMap? }`. Uses the Insights Usage Query API |
| `POST /api/teams/{teamId}/inactive-users/report` | Multipart form with `file`, `days` and `productMap?`. Uses an uploaded report |
| `POST /api/teams/{teamId}/assignments/remove` | `{ items: [{ userId, productId }] }`. Returns a status per item: `Removed`, `Inherited` or `Failed` |

Every team endpoint checks again that you administer the team. The Business Success Plan Reporting API source also
requires the team to have Reporting API access. Uploading a report and removing licenses don't, so teams without
that benefit can use a downloaded report.

### Matching usage with assignments

- **Teams** are resolved with Team Admin `GET /teams`. The team in a report's team column is matched to the team
  name (or the Insights alias or the team id).
- **Users** are matched by email (case-insensitive). The email comes from Team Admin `GET /teams/{teamId}/users`,
  which also gives the `userId` used to remove the assignment.
- **Products** are matched by name. Each Team Admin product pool (for example *AutoCAD - including specialized
  toolsets* or *AEC Collection*) is compared with the subscription **offering** in the usage data:
  - Insights `productName`.
  - `offering_name` in legacy exports.
  - `Product` in 2026 exports.

  Names are compared after normalization (case, punctuation, "subscription" and "including ..." are ignored). If a
  product does not match, or matches the wrong product, fix it in the **Product mapping** panel.
- An assignment is **inactive** when the user has no usage of the product on or after `as-of date - N days`:
  - For the API, the as-of date is today (UTC).
  - For a report, it is the latest usage date in the file.
- Only **DIRECT** assignments can be removed. Assignments inherited from the whole team are shown
  but cannot be selected. Change them in Autodesk Account.

### Accepted reports

Upload a `.csv`, `.xlsx` or `.zip` file. Each table needs an email column, an offering/product column and a usage
date column. Header names are matched without regard to case, spaces or underscores.

| Report | Columns used |
| --- | --- |
| Export Usage API / Autodesk Account **usage** (`usage.csv`, or `all.zip` with subscriptions/users/usage) | `email`, `offering_name`, `day_used`, `team_alias` |
| 2026 **Seat usage** export | `Email`, `Product`, `Day used`, `Team` |
| **Usage report** (rolled up) | `email`, `offering_name`, `last_accessed` |
| Tables exported from [aps-business-success-plan-report](https://github.com/autodesk-platform-services/aps-business-success-plan-report/blob/main/DATA-TRANSFORMATION.md) | `Email`, `Product`, `Latest` |

- If the report has a team column, only rows for the selected team are used.
- Tables without usage, such as `subscriptions.csv` and `users.csv`, are skipped.
- Make sure the report covers at least N days. The app warns when daily data starts after the start of the window.

## Limitations

- **Personal data:** if a team hashes personal data, the Insights API and the exports don't include emails. Usage
  then can't be matched to Team Admin users. Turn on personal data in Autodesk Account, under Reporting settings.
- **Rate limits:** Team Admin allows 60 requests per minute per operation per client ID. Insights limits are per
  user. Throttled calls (429) are retried after `Retry-After`, so removing many licenses can take a while.
- **Pagination limit:** this is a sample, so each paginated list is capped at 5 pages of 100 items, which is 500
  users, pools or usage rows. The Team Admin lists and the Usage Query results are both capped. Larger teams are
  truncated, and the result includes a warning when that happens. Users beyond the cap can be listed as inactive. To lift the limit, change
  `MaxPages` in `Models/APS.Insights.cs`. For large teams, prefer the exported report, which isn't capped.
- **Assignment lag:** assignment changes can take a moment to show in team-wide results.
- **Token storage:** tokens are kept in encrypted cookies through ASP.NET Core Data Protection. For deployments with
  several instances, configure a shared Data Protection key store.

## Troubleshooting

- **"You are not an administrator of any team with Business Success Plan Reporting API access"**: check the role in
  Autodesk Account and that the team has the Business Success Plan reporting benefit.
- **403 / AUTH-001**: the APS application has not been granted access to the Team Admin or Insights API.
- **Callback mismatch**: `APS_CALLBACK_URL` must exactly match the callback URL in the APS application.

## License

This sample is licensed under the terms of the [MIT License](http://opensource.org/licenses/MIT).
Please see the [LICENSE](LICENSE) file for full details.

## Written by

Joao Martins [in/jpornelas](https://linkedin.com/in/jpornelas), [Developer Advocate](http://aps.autodesk.com)