# Dashboard Module — User Guide

> WTM 8.x | Last updated: 2026-03-12

This guide is for business users who create and manage dashboards in a WTM application.

## Viewing Dashboards

Navigate to the dashboard section of your application. You will see a list of dashboards available to you:

- **Your dashboards** — dashboards you created
- **Shared dashboards** — dashboards shared with your role or marked as public
- **Admin view** — if you have the Admin role, you can see all dashboards

Click any dashboard title to open it. Widgets display data automatically and refresh on a timer (default: every 60 seconds).

## Creating a Dashboard

1. Click **New Dashboard** in the toolbar
2. Enter a title for your dashboard
3. The dashboard opens in edit mode with an empty grid

You are the **owner** of dashboards you create. Only you and administrators can edit or delete them.

## Adding and Editing Widgets

### Adding a widget

1. Enter **Edit Mode** by clicking the edit (pencil) icon in the toolbar
2. Click **Add Widget**
3. Choose the widget type:
   - **KPI** — a single number with an optional trend indicator (up/down arrow)
   - **Chart** — bar, line, or pie chart powered by your data
   - **Table** — a data table with sortable columns
   - **Progress** — a progress bar showing completion percentage
   - **List** — a simple list of items with status indicators
   - **Embed** — an embedded page (same-origin only by default)
4. Select a **data source** for the widget
5. Configure the widget title and any source-specific options

### Moving and resizing widgets

In edit mode, widgets can be:

- **Dragged** to a new position on the grid
- **Resized** by dragging the bottom-right corner

The grid layout uses a 12-column system. Changes are saved when you click **Save**.

### Removing a widget

In edit mode, click the **X** button on the widget card to remove it.

## Using Filters

Some dashboards have a **filter bar** at the top. Filters let you narrow the data shown across all widgets at once.

For example, a "Region" filter might let you switch between North, South, East, and West — all charts and KPIs on the dashboard update to show data for the selected region.

To use filters:
1. Select a value from any filter dropdown
2. All widgets automatically refresh with the filtered data
3. To reset, select the default option or clear the filter

## Widget Linkage

Some dashboards have **linked widgets**. This means clicking on one widget can filter another.

For example:
- Click a KPI showing "Electronics" revenue
- A linked chart automatically filters to show only Electronics data

Linked interactions are configured by the dashboard creator and happen automatically — no extra steps needed.

## Sharing and Permissions

### Who can see your dashboard

By default, dashboards are **private** — only you and administrators can see them.

To share a dashboard, the dashboard creator (or an admin) can set sharing options:

| Sharing mode | Who can view |
|-------------|-------------|
| **Private** (default) | Only the owner and admins |
| **Public** | All authenticated users in the application |
| **Role-based** | Users with specific roles (e.g., Manager, Finance) |

### Who can edit

Only the **owner** and users with the **Admin** role can edit or delete a dashboard. Shared viewers have read-only access.

## Auto-Refresh

Dashboards automatically refresh their widget data on a timer. The default interval is 60 seconds, but each dashboard can have its own refresh rate.

- During refresh, widgets show a brief loading indicator
- If a widget fails to load data, it shows an error message with a **Retry** button
- After 3 consecutive failures, a widget stops retrying automatically — click **Retry** to try again

## Multi-Tenant Support

If your application uses multi-tenancy, dashboards are isolated per tenant:

- You only see dashboards belonging to your tenant
- Dashboards created by you are automatically assigned to your current tenant
- Switching tenants shows a different set of dashboards

This isolation is automatic — no configuration is needed from users.

## Tips

- **Bookmark dashboards** — use your browser's bookmark feature on any dashboard URL for quick access
- **Fullscreen** — most browsers support F11 for a distraction-free dashboard view
- **Refresh manually** — click the refresh icon in the toolbar to update all widgets immediately
- **Check data freshness** — hover over a widget to see when its data was last loaded
