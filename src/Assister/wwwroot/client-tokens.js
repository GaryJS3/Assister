(() => {
    'use strict';
    const $ = id => document.getElementById(id);
    const node = (tag, text) => { const el = document.createElement(tag); if (text != null) el.textContent = text; return el; };
    let admin = '';
    const notice = text => { $('token-notice').textContent = text; };
    const hideIssued = () => { $('issued-token').value = ''; $('issued').hidden = true; };
    function lock() {
        admin = ''; $('admin-token').value = ''; hideIssued(); $('credentials').replaceChildren();
        $('management').hidden = true; $('unlock-panel').hidden = false;
    }
    async function api(path = '', method = 'GET', body) {
        const response = await fetch('/api/devices/credentials' + path, {
            method, cache:'no-store', headers:{Authorization:'Bearer ' + admin, 'Content-Type':'application/json'},
            body: body == null ? undefined : JSON.stringify(body)
        });
        if (response.status === 401) { lock(); throw Error('Administrator credential required or rejected.'); }
        if (!response.ok) { const value = await response.json().catch(() => ({})); throw Error(value.error || `Request failed (${response.status}).`); }
        return response.status === 204 ? null : response.json();
    }
    function issued(value) {
        $('issued-title').textContent = 'Token for ' + value.clientId;
        $('issued-token').value = value.token; $('issued').hidden = false;
        $('issued').scrollIntoView({behavior:'smooth', block:'center'});
    }
    async function refresh() {
        const entries = await api();
        $('credentials').replaceChildren();
        for (const entry of entries) {
            const row = node('article'); row.className = 'credential-row';
            row.append(node('h3', entry.clientId), node('p', `${entry.revoked ? 'Revoked' : 'Active'} · ${entry.managed ? 'Managed here' : 'Server configuration'} · Owner: ${entry.owner}`));
            const form = node('form'), label = node('label', 'Owner'), owner = node('input');
            owner.value = entry.owner; owner.required = true; owner.maxLength = 128; label.append(owner);
            const save = node('button', 'Reassign owner'); save.type = 'submit';
            form.append(label, save);
            form.onsubmit = async e => {
                e.preventDefault();
                if (owner.value.trim() === entry.owner) return;
                if (!confirm(`Reassign ${entry.clientId} to owner ${owner.value.trim()}? This changes access to conversations and notifications. Existing history stays with its original owner.`)) return;
                save.disabled = true;
                try { await api('/' + encodeURIComponent(entry.clientId) + '/owner', 'PUT', {owner:owner.value}); notice('Owner changed. The device will register again on renewal.'); await refresh(); }
                catch (error) { notice(error.message); } finally { save.disabled = false; }
            };
            const rotate = node('button', entry.revoked ? 'Issue replacement token' : 'Rotate token'); rotate.type = 'button';
            rotate.onclick = async () => {
                if (!confirm(`Issue a replacement token for ${entry.clientId}? Its current token will stop working immediately.`)) return;
                rotate.disabled = true;
                try { hideIssued(); issued(await api('/' + encodeURIComponent(entry.clientId) + '/rotate', 'POST')); await refresh(); notice('Replacement issued. Update the client’s bearer token.'); }
                catch (error) { notice(error.message); } finally { rotate.disabled = false; }
            };
            form.append(rotate);
            if (!entry.revoked) {
                const revoke = node('button', 'Revoke'); revoke.type = 'button'; revoke.className = 'token-danger';
                revoke.onclick = async () => {
                    if (!confirm(`Revoke ${entry.clientId}? It will lose access immediately. Conversation history is retained.`)) return;
                    revoke.disabled = true;
                    try { hideIssued(); await api('/' + encodeURIComponent(entry.clientId), 'DELETE'); await refresh(); notice('Token revoked. Server environment credentials stay overridden across restarts.'); }
                    catch (error) { notice(error.message); } finally { revoke.disabled = false; }
                };
                form.append(revoke);
            }
            row.append(form); $('credentials').append(row);
        }
    }
    $('unlock-form').onsubmit = async e => {
        e.preventDefault(); admin = $('admin-token').value; $('admin-token').value = '';
        try { await refresh(); $('management').hidden = false; $('unlock-panel').hidden = true; notice('Token management unlocked.'); }
        catch (error) { notice(error.message); }
    };
    $('create-form').onsubmit = async e => {
        e.preventDefault(); const form = e.currentTarget, button = form.querySelector('button'); button.disabled = true;
        try { hideIssued(); issued(await api('', 'POST', {clientId:form.elements.clientId.value, owner:form.elements.owner.value})); form.reset(); await refresh(); notice('Client created. Enter the new token on your device.'); }
        catch (error) { notice(error.message); } finally { button.disabled = false; }
    };
    $('copy').onclick = async () => {
        try { await navigator.clipboard.writeText($('issued-token').value); notice('Token copied.'); }
        catch { $('issued-token').focus(); $('issued-token').select(); notice('Select and copy the token manually.'); }
    };
    $('hide-token').onclick = hideIssued;
    $('lock').onclick = () => { lock(); notice('Token management locked.'); };
    $('reload').onclick = () => refresh().catch(error => notice(error.message));
    window.addEventListener('pagehide', lock);
})();
