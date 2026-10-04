# Management UI shell

The current management UI is static HTML served by ASP.NET Core. All pages share `wwwroot/app-shell.js` and `wwwroot/app-shell.css` for branding, navigation, active-page indication and responsive header layout.

To add a page, include `app-shell.css` after its page styles, add `<header data-app-shell></header>` before the main content, and load `app-shell.js` before the page's own script at the end of the body. Add its route and label to the navigation list in `app-shell.js`; do not copy navigation links into individual pages.

The shell preserves any existing elements inside the header as page-specific status content. The pipeline debugger uses this for its live connection indicator. Page CSS should style content rather than the shared header. Theme variables and common content styles remain in `dashboard.css`.
