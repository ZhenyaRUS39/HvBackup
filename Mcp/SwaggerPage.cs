namespace HvBackup.Mcp;

/// <summary>
/// Страница Swagger: самодостаточная (без интернета и внешних скриптов),
/// читает документ OpenAPI и показывает все операции с описаниями.
/// </summary>
public static class SwaggerPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ГиперВ Резерв — Swagger</title>
<style>
  :root {
    --bg: #f4f5f7; --card: #ffffff; --text: #1b1b1b; --muted: #6a6a6a;
    --line: #e2e4e8; --accent: #0067c0; --get: #0f7b3f; --post: #0067c0;
    --put: #a06000; --delete: #c42b1c; --code: #f6f7f9;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text);
         font-family: "Segoe UI", system-ui, sans-serif; font-size: 14px; line-height: 1.55; }
  header { background: var(--card); border-bottom: 1px solid var(--line); padding: 22px 28px; }
  header h1 { margin: 0 0 6px; font-size: 22px; }
  header p { margin: 4px 0; color: var(--muted); max-width: 900px; }
  .ver { display: inline-block; background: var(--code); border: 1px solid var(--line);
         border-radius: 12px; padding: 1px 10px; font-size: 12px; color: var(--muted); }
  .note { margin-top: 12px; padding: 10px 14px; background: #fff8e1; border: 1px solid #f0dca0;
          border-radius: 6px; color: #6b4e00; max-width: 900px; }
  main { padding: 24px 28px 60px; max-width: 1180px; }
  h2.tag { margin: 26px 0 10px; font-size: 17px; color: var(--muted); font-weight: 600;
           border-bottom: 1px solid var(--line); padding-bottom: 6px; }
  .op { background: var(--card); border: 1px solid var(--line); border-radius: 8px;
        margin-bottom: 14px; overflow: hidden; }
  .op-head { display: flex; align-items: center; gap: 12px; padding: 12px 16px; cursor: pointer; }
  .op-head:hover { background: #fafbfc; }
  .m { font-weight: 700; font-size: 12px; letter-spacing: .5px; color: #fff; border-radius: 4px;
       padding: 3px 9px; min-width: 64px; text-align: center; }
  .m.get { background: var(--get); } .m.post { background: var(--post); }
  .m.put { background: var(--put); } .m.delete { background: var(--delete); }
  .path { font-family: Consolas, monospace; font-size: 13px; }
  .sum { color: var(--muted); margin-left: auto; text-align: right; }
  .body { padding: 0 16px 16px; display: none; }
  .op.open .body { display: block; }
  .desc { white-space: pre-wrap; margin: 8px 0 14px; }
  h3 { font-size: 13px; text-transform: uppercase; letter-spacing: .6px; color: var(--muted);
       margin: 16px 0 8px; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
  th { color: var(--muted); font-weight: 600; width: 130px; }
  input[type=text], textarea { width: 100%; padding: 6px 8px; border: 1px solid var(--line);
         border-radius: 5px; font-family: Consolas, monospace; font-size: 12.5px; background: var(--code); }
  textarea { min-height: 110px; }
  button { background: var(--accent); color: #fff; border: 0; border-radius: 6px; padding: 8px 18px;
           font-size: 13px; cursor: pointer; margin-top: 10px; }
  button:hover { background: #1975c5; }
  button.ghost { background: #fff; color: var(--text); border: 1px solid var(--line); }
  .resp { background: #1e1e1e; color: #e8e8e8; border-radius: 6px; padding: 12px; margin-top: 10px;
          font-family: Consolas, monospace; font-size: 12.5px; white-space: pre-wrap;
          max-height: 460px; overflow: auto; }
  .status { font-weight: 600; }
  .status.ok { color: #3fb950; } .status.err { color: #f85149; }
  .req { color: var(--muted); font-size: 12.5px; font-family: Consolas, monospace; }
  .req b { color: var(--text); }
  code { background: var(--code); padding: 1px 5px; border-radius: 4px; font-size: 12.5px; }
</style>
</head>
<body>
<header>
  <h1 id="title">ГиперВ Резерв — API</h1>
  <p id="desc">Загрузка описания…</p>
  <p><span class="ver" id="ver"></span> <span class="ver" id="servers"></span></p>
  <div class="note">Операции с виртуальными машинами Hyper-V выполняются от имени администратора.
    Если сервер запущен без прав, он вернёт понятную ошибку о недостатке прав доступа.</div>
</header>
<main id="main"><p>Загрузка…</p></main>

<script>
const esc = s => String(s === undefined || s === null ? '' : s)
  .replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');

function sample(schema) {
  if (!schema) return "";
  if (schema.enum) return schema.enum[0];
  switch (schema.type) {
    case 'string': return schema.default !== undefined ? schema.default : "";
    case 'integer': return schema.default !== undefined ? schema.default : 0;
    case 'number': return schema.default !== undefined ? schema.default : 0;
    case 'boolean': return schema.default !== undefined ? schema.default : false;
    case 'array': return [];
    case 'object': {
      const o = {};
      const props = schema.properties || {};
      for (const k in props) {
        if (schema.required && schema.required.indexOf(k) < 0) continue;
        o[k] = sample(props[k]);
      }
      return o;
    }
  }
  return "";
}

function render(doc) {
  document.getElementById('title').textContent = doc.info.title || 'API';
  document.getElementById('desc').textContent = doc.info.description || '';
  document.getElementById('ver').textContent = 'версия ' + (doc.info.version || '');
  document.getElementById('servers').textContent = (doc.servers || []).map(s => s.url).join(' · ');

  const main = document.getElementById('main');
  main.innerHTML = '';

  const groups = {};
  for (const path in doc.paths) {
    for (const method in doc.paths[path]) {
      const op = doc.paths[path][method];
      const tag = (op.tags && op.tags[0]) || 'Прочее';
      (groups[tag] = groups[tag] || []).push({ path, method, op });
    }
  }

  for (const tag in groups) {
    const h = document.createElement('h2');
    h.className = 'tag';
    h.textContent = tag;
    main.appendChild(h);

    for (const item of groups[tag]) main.appendChild(card(item));
  }
}

function card(item) {
  const { path, method, op } = item;
  const el = document.createElement('div');
  el.className = 'op';

  const head = document.createElement('div');
  head.className = 'op-head';
  head.innerHTML = '<span class="m ' + method + '">' + method.toUpperCase() + '</span>' +
    '<span class="path">' + esc(path) + '</span>' +
    '<span class="sum">' + esc(op.summary || '') + '</span>';
  el.appendChild(head);

  const body = document.createElement('div');
  body.className = 'body';

  const desc = document.createElement('div');
  desc.className = 'desc';
  desc.textContent = op.description || '';
  body.appendChild(desc);

  const params = op.parameters || [];
  if (params.length) {
    body.appendChild(title('Параметры'));
    const table = document.createElement('table');
    for (const p of params) {
      const tr = document.createElement('tr');
      tr.innerHTML =
        '<th>' + esc(p.name) + (p.required ? ' *' : '') + '</th>' +
        '<td><div style="color:#6a6a6a;margin-bottom:4px">' + esc(p.description || '') +
        (p.schema && p.schema.default !== undefined ? ' (по умолчанию: ' + esc(p.schema.default) + ')' : '') +
        '</div><input type="text" data-param="' + esc(p.name) + '" data-in="' + esc(p.in) + '" ' +
        'placeholder="' + esc(p.schema && p.schema.default !== undefined ? p.schema.default : '') + '"></td>';
      table.appendChild(tr);
    }
    body.appendChild(table);
  }

  let bodyInput = null;
  if (op.requestBody) {
    body.appendChild(title('Тело запроса (JSON)'));
    bodyInput = document.createElement('textarea');
    const schema = op.requestBody.content['application/json'].schema;
    bodyInput.value = JSON.stringify(sample(schema), null, 2);
    body.appendChild(bodyInput);
  }

  body.appendChild(title('Ответ'));
  const respInfo = document.createElement('div');
  respInfo.className = 'req';
  respInfo.innerHTML = Object.keys(op.responses || {}).map(c =>
    '<b>' + c + '</b> — ' + esc(op.responses[c].description || '')).join('<br>');
  body.appendChild(respInfo);

  const out = document.createElement('div');
  out.className = 'resp';
  out.style.display = 'none';

  const btn = document.createElement('button');
  btn.textContent = 'Выполнить';
  btn.onclick = async () => {
    let url = path;
    const query = new URLSearchParams();
    let payload = null;

    for (const input of body.querySelectorAll('[data-param]')) {
      const name = input.getAttribute('data-param');
      const where = input.getAttribute('data-in');
      const value = input.value.trim();
      if (!value) continue;
      if (where === 'path') url = url.replace('{' + name + '}', encodeURIComponent(value));
      else query.set(name, value);
    }

    if (bodyInput) {
      const text = bodyInput.value.trim();
      if (text) {
        try { payload = JSON.parse(text); }
        catch (e) { show(out, false, 'Не разобран JSON: ' + e.message); return; }
      }
    }

    const finalUrl = url + (query.toString() ? '?' + query.toString() : '');
    show(out, true, 'Выполняется ' + method.toUpperCase() + ' ' + finalUrl + ' …', false);

    try {
      const res = await fetch(finalUrl, {
        method: method.toUpperCase(),
        headers: payload ? { 'Content-Type': 'application/json' } : {},
        body: payload ? JSON.stringify(payload) : undefined
      });
      const text = await res.text();
      let pretty = text;
      try { pretty = JSON.stringify(JSON.parse(text), null, 2); } catch (e) {}
      show(out, res.ok, 'HTTP ' + res.status + ' ' + res.statusText + '\n\n' + pretty);
    } catch (e) {
      show(out, false, 'Ошибка запроса: ' + e.message);
    }
  };
  body.appendChild(btn);
  body.appendChild(out);

  head.onclick = () => el.classList.toggle('open');
  el.appendChild(body);
  return el;
}

function title(text) {
  const h = document.createElement('h3');
  h.textContent = text;
  return h;
}

function show(el, ok, text, withStatus) {
  el.style.display = 'block';
  el.textContent = text;
  if (withStatus !== false) el.className = 'resp';
}

fetch('/swagger/v1/swagger.json')
  .then(r => r.json())
  .then(doc => render(doc))
  .catch(e => {
    document.getElementById('main').innerHTML =
      '<p>Не удалось загрузить описание API: ' + esc(e.message) + '</p>';
  });
</script>
</body>
</html>
""";
}
