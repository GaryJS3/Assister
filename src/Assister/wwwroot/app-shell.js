'use strict';

// All management pages use this navigation. Load before each page's own script.
(() => {
  const header = document.querySelector('[data-app-shell]');
  if (!header) return;
  const status = Array.from(header.childNodes);
  header.classList.add('app-header');
  const brand = document.createElement('a');
  brand.className = 'app-brand';
  brand.href = '/';
  brand.setAttribute('aria-label', 'Assister home');
  brand.append('A');
  const name = document.createElement('span');
  name.textContent = ' / ASSISTER';
  brand.append(name);
  const nav = document.createElement('nav');
  nav.className = 'app-nav';
  nav.setAttribute('aria-label', 'Main navigation');
  const current = location.pathname === '/index.html' ? '/' : location.pathname;
  for (const [href, label] of [['/', 'Pipeline debugger'], ['/chat.html', 'Conversation'], ['/satellites.html', 'Satellites'], ['/intents.html', 'Intents'], ['/integrations.html', 'Integrations']]) {
    const link = document.createElement('a');
    link.href = href;
    link.textContent = label;
    if (current === href) link.setAttribute('aria-current', 'page');
    nav.append(link);
  }
  const tools = document.createElement('div');
  tools.className = 'app-header-tools';
  tools.append(nav);
  if (status.some(child => child.nodeType === Node.ELEMENT_NODE)) {
    const slot = document.createElement('div');
    slot.className = 'app-header-status';
    slot.append(...status);
    tools.append(slot);
  }
  header.replaceChildren(brand, tools);
})();
