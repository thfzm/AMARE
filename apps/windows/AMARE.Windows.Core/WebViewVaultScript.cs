namespace Amare.Windows.Core;

/// <summary>
/// Top-level-only WebView2 content script for login-form metadata, user-approved
/// password capture, and explicit fill. The script is dormant until the native
/// host sends a per-document init token and generation. It never reads a
/// password before a native read command, never submits, never clicks, and never
/// synthesizes keyboard input.
/// </summary>
public static class WebViewVaultScript
{
    public const string Script = """
(() => {
  'use strict';
  if (window.top !== window) return;
  if (!window.chrome || !chrome.webview || !chrome.webview.postMessage) return;

  const forms = new Map();
  let nativeToken = null;
  let documentGeneration = 0;
  let ready = false;

  const randomHex = () => {
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
  };
  const isHex = (value, min, max) =>
    typeof value === 'string'
    && value.length >= min
    && value.length <= max
    && /^[0-9a-f]+$/i.test(value);
  const currentOrigin = () => {
    try {
      return location.protocol === 'https:' ? location.origin : null;
    } catch {
      return null;
    }
  };
  const isVisible = element => {
    if (!(element instanceof HTMLInputElement)
        || element.disabled
        || element.readOnly
        || !element.isConnected) {
      return false;
    }
    const style = getComputedStyle(element);
    if (style.display === 'none'
        || style.visibility === 'hidden'
        || style.opacity === '0') {
      return false;
    }
    const rect = element.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  };
  const actionOrigin = form => {
    try {
      const url = new URL(form.getAttribute('action') || location.href, location.href);
      return url.origin;
    } catch {
      return null;
    }
  };
  const usernameField = form => {
    const candidates = Array.from(form.elements).filter(element =>
      element instanceof HTMLInputElement
      && element.form === form
      && isVisible(element)
      && ['text', 'email', 'tel', ''].includes((element.type || 'text').toLowerCase()));
    const strong = candidates.filter(element =>
      /username|user|email|login|account/i.test(
        `${element.name} ${element.id} ${element.autocomplete} ${element.placeholder}`));
    if (strong.length === 1) return strong[0];
    if (candidates.length === 1) return candidates[0];
    return null;
  };
  const eligibleForm = form => {
    if (!(form instanceof HTMLFormElement) || !form.isConnected) return null;
    const origin = currentOrigin();
    if (!origin || actionOrigin(form) !== origin) return null;
    const passwords = Array.from(form.elements).filter(element =>
      element instanceof HTMLInputElement
      && element.type === 'password'
      && element.form === form
      && isVisible(element));
    if (passwords.length === 0) return null;
    const username = usernameField(form);
    let token = form.__amareVaultToken;
    if (!token) {
      token = randomHex();
      form.__amareVaultToken = token;
    }
    const entry = forms.get(token);
    if (!entry) {
      forms.set(token, { form, password: passwords[0], username });
    } else {
      entry.form = form;
      entry.password = passwords[0];
      entry.username = username;
    }
    return {
      origin,
      actionOrigin: origin,
      formToken: token,
      username: username ? username.value || '' : '',
      ambiguous: passwords.length !== 1 || username === null,
      hasPassword: true,
      hasUsername: username !== null
    };
  };
  const validateEntry = (entry) => {
    const origin = currentOrigin();
    if (!origin
        || !entry
        || !entry.form.isConnected
        || actionOrigin(entry.form) !== origin
        || !(entry.password instanceof HTMLInputElement)
        || entry.password.type !== 'password'
        || entry.password.form !== entry.form
        || !entry.form.contains(entry.password)
        || !isVisible(entry.password)
        || !(entry.username instanceof HTMLInputElement)
        || entry.username.form !== entry.form
        || !entry.form.contains(entry.username)
        || !isVisible(entry.username)) {
      return false;
    }
    const passwords = Array.from(entry.form.elements).filter(element =>
      element instanceof HTMLInputElement
      && element.type === 'password'
      && element.form === entry.form
      && isVisible(element));
    return passwords.length === 1 && passwords[0] === entry.password;
  };
  const detect = () => {
    const origin = currentOrigin();
    if (!origin) {
      return {
        ok: false, origin: '', actionOrigin: '', formToken: '', username: '',
        ambiguous: false, hasPassword: false, hasUsername: false
      };
    }
    const candidates = [];
    for (const form of document.forms) {
      const descriptor = eligibleForm(form);
      if (descriptor) candidates.push(descriptor);
    }
    if (candidates.length !== 1) {
      return {
        ok: false, origin, actionOrigin: origin, formToken: '', username: '',
        ambiguous: candidates.length > 1, hasPassword: false, hasUsername: false
      };
    }
    return { ...candidates[0], ok: true };
  };
  const readSecret = formToken => {
    const entry = forms.get(formToken);
    if (!validateEntry(entry)) {
      return { ok: false };
    }
    return { ok: true, username: entry.username.value || '', password: entry.password.value };
  };
  const setValue = (element, value) => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setter.call(element, value);
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const send = (message) => {
    try {
      const payload = {
        ...message,
        version: 1,
        generation: documentGeneration,
        token: nativeToken,
        msgId: randomHex()
      };
      chrome.webview.postMessage(payload);
    } catch {
      // The page may have navigated; nothing else to do.
    }
  };

  document.addEventListener('submit', event => {
    if (!ready) return;
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const descriptor = eligibleForm(form);
    if (!descriptor || descriptor.ambiguous) return;
    send({
      type: 'amare_login_candidate',
      origin: descriptor.origin,
      actionOrigin: descriptor.actionOrigin,
      formToken: descriptor.formToken,
      username: descriptor.username,
      ambiguous: false
    });
  }, true);

  chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if (!message || typeof message !== 'object' || message.version !== 1) return;
    if (message.type === 'amare_init') {
      if (message.origin !== currentOrigin()
          || !isHex(message.token, 32, 128)
          || !Number.isSafeInteger(message.generation)
          || message.generation < 1) {
        return;
      }
      nativeToken = message.token;
      documentGeneration = message.generation;
      ready = true;
      forms.clear();
      send({ type: 'amare_ready' });
      return;
    }
    if (!ready
        || !isHex(message.opId, 16, 64)
        || message.token !== nativeToken
        || message.generation !== documentGeneration) {
      return;
    }
    if (message.type === 'amare_detect') {
      const descriptor = detect();
      send({
        type: 'amare_detect_result',
        opId: message.opId,
        ok: descriptor.ok,
        origin: descriptor.origin,
        actionOrigin: descriptor.actionOrigin,
        formToken: descriptor.formToken,
        username: descriptor.username,
        ambiguous: descriptor.ambiguous,
        hasPassword: descriptor.hasPassword,
        hasUsername: descriptor.hasUsername
      });
      return;
    }
    if (message.type === 'amare_read_secret') {
      const secret = readSecret(message.formToken);
      send({
        type: 'amare_secret',
        opId: message.opId,
        formToken: message.formToken,
        username: secret.ok ? secret.username : '',
        password: secret.ok ? secret.password : ''
      });
      return;
    }
    if (message.type === 'amare_fill') {
      const entry = forms.get(message.formToken);
      if (!validateEntry(entry)
          || typeof message.username !== 'string'
          || typeof message.password !== 'string') {
        send({ type: 'amare_fill_result', opId: message.opId, ok: false });
        return;
      }
      try {
        setValue(entry.username, message.username);
        setValue(entry.password, message.password);
        send({ type: 'amare_fill_result', opId: message.opId, ok: true });
      } catch {
        send({ type: 'amare_fill_result', opId: message.opId, ok: false });
      }
    }
  });
})();
""";
}
