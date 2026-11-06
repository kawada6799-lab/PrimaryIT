// 健診結果作成：PC とタブレットで同じ画面。サーバー（kenshin.exe）の API を呼ぶだけ。
(() => {
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => Array.from(el.querySelectorAll(s));
const h = (tag, attrs = {}, ...children) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === 'class') el.className = v; else if (k === 'html') el.innerHTML = v; else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined) el.setAttribute(k, v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined) el.append(c.nodeType ? c : document.createTextNode(String(c)));
  return el;
};
const S = { masters: null, status: null, view: 'day', date: today(), exams: [], exam: null, tab: 'measure', busy: false, pendingSaves: {} };

function today() { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }
function toast(msg, err) { const t = h('div', { class: 'toast' + (err ? ' err' : '') }, msg); document.body.append(t); setTimeout(() => t.remove(), err ? 5000 : 2500); }
async function api(path, opts = {}) {
  const o = Object.assign({ headers: {} }, opts);
  if (o.body && typeof o.body !== 'string' && !(o.body instanceof ArrayBuffer)) { o.body = JSON.stringify(o.body); o.headers['content-type'] = 'application/json'; }
  const r = await fetch(path, o);
  if (r.status === 423) { const j = await r.json(); showOverlay(j.error); throw new Error(j.error); }
  if (r.status === 401) { const j = await r.json().catch(() => ({})); location.href = j.pin ? '/pin' : '/unlock'; throw new Error('認証'); }
  if (!r.ok) { const j = await r.json().catch(() => ({ error: r.statusText })); throw new Error(j.error || r.statusText); }
  return r.status === 204 ? null : r.json();
}
function showOverlay(text) { $('#overlayText').innerHTML = '<b>停止中</b><br>' + text + '<br><br><span class="note">解消されると自動で再開します。</span>'; $('#overlay').classList.remove('hidden'); }
function hideOverlay() { $('#overlay').classList.add('hidden'); }

async function pollStatus() {
  try {
    const st = await (await fetch('/api/status')).json();
    S.status = st;
    if (st.blocked) showOverlay(st.reason); else hideOverlay();
    $('#devbanner').classList.toggle('hidden', st.guardEnabled);
    $('#status').textContent = (st.clinic?.name || '') + '　写真読取: ' + (st.ocr?.available ? '有効' : '無効') + '　v' + (st.version || '') + (st.built ? '（' + st.built + '）' : '');
    const quit = $('#quit');
    if (quit && st.local) { quit.style.display = ''; quit.onclick = async () => { if (!confirm('プログラムを終了します（バックアップを書いてから止まります）。よろしいですか？')) return; try { await api('/api/shutdown', { method: 'POST' }); } catch (e) { } document.body.innerHTML = '<div style="padding:40px;font-size:18px">終了しました。この画面は閉じてください。もう一度使うときはデスクトップの「健診結果作成」をダブルクリック。</div>'; }; }
  } catch (e) { }
}

// ---------- 名前・ラベル ----------
const catName = code => (S.masters.categories.find(c => c.code === code && !c.section) || {}).name || code || '';
const sectionLabel = (section, code) => (S.masters.sectionLabels[section] || {})[code] || ((S.masters.categories.find(c => c.code === code && c.section === section) || S.masters.categories.find(c => c.code === code && !c.section) || {}).name) || code || '';
// その項目で選べる区分：個別コード（計測の肥満度、視力）があればそれと、同じ番号の無い共通区分
const codesFor = section => { const own = S.masters.categories.filter(c => c.section === section); const common = S.masters.categories.filter(c => !c.section); return own.length ? own.concat(common.filter(c => !own.some(o => o.code === c.code))) : common; };
const commentText = code => (S.masters.comments.find(c => c.code === code) || {}).text || '';
const statusBadge = s => h('span', { class: 'badge ' + (s === '確定' || s === '郵送済' ? 'done' : s === '判定済' ? 'ok' : s === '入力中' ? 'wait' : '') }, s);
const judgBadge = j => j ? h('span', { class: 'badge ' + (j.manual ? 'manual' : 'auto') }, j.manual ? '手動' : '自動') : null;
const isLocked = () => S.exam && (S.exam.exam.status === '確定' || S.exam.exam.status === '郵送済');

// ---------- 左：当日の受診者 ----------
async function loadDay() {
  S.exams = await api('/api/exams?date=' + S.date);
  const list = $('#ptlist'); list.innerHTML = '';
  if (S.exams.length === 0) list.append(h('div', { class: 'note', style: 'padding:12px' }, 'この日の受診者はいません。「＋ 受診者」から登録します。'));
  for (const e of S.exams) {
    list.append(h('div', { class: 'pt' + (S.exam && S.exam.exam.id === e.id ? ' sel' : ''), onclick: () => openExam(e.id) },
      h('div', {}, h('div', { class: 'nm' }, e.name), h('div', { class: 'sub' }, `${e.sex} ${e.age ?? ''}歳 ID ${e.falcoId || e.chartNo || '-'} ／ ${e.course}`)), statusBadge(e.status)));
  }
  try { const imp = await api('/api/import/files'); const n = imp.files.filter(f => !f.imported).length; $('#sidenote').textContent = `取込フォルダ：${imp.dir}\n未処理：${n} 件`; } catch (e) { }
}

// ---------- 受診者の追加 ----------
function patientDialog(existing) {
  const p = existing || { name: '', kana: '', sex: '男', birthDate: '', falcoId: '', chartNo: '', employeeNo: '', office: '', dept: '' };
  const f = {};
  const field = (label, key, type = 'text', extra = {}) => h('div', { class: 'row' }, h('label', {}, label), f[key] = h('input', Object.assign({ type, value: p[key] || '' }, extra)));
  const search = h('div', { class: 'list hidden' });
  const box = h('div', { class: 'card', style: 'max-width:620px' },
    h('h4', {}, existing ? '受診者の情報' : '受診者を登録'),
    h('div', { class: 'b' },
      existing ? null : h('div', { class: 'row' }, h('label', {}, '既存から探す'), h('input', { type: 'text', placeholder: '氏名・カナ・ID', oninput: async ev => {
        const q = ev.target.value.trim(); if (!q) { search.classList.add('hidden'); return; }
        const list = await api('/api/patients?q=' + encodeURIComponent(q)); search.innerHTML = ''; search.classList.toggle('hidden', list.length === 0);
        for (const x of list) search.append(h('div', { onclick: async () => { await createExamFor(x.id); } }, `${x.name}（${x.kana}）${x.sex} ${x.birthDate} ID ${x.falcoId || x.chartNo}`));
      } })),
      search,
      field('氏名 *', 'name'), field('カナ', 'kana'),
      h('div', { class: 'row' }, h('label', {}, '性別'), f.sex = h('select', {}, h('option', {}, '男'), h('option', {}, '女'))),
      field('生年月日', 'birthDate', 'text', { placeholder: '1974-05-01 または S49.5.1' }),
      field('個人ID（ファルコ）', 'falcoId'), field('診察券番号', 'chartNo'), field('社員番号', 'employeeNo'), field('事業所', 'office'), field('所属', 'dept'),
      existing ? null : h('div', { class: 'row' }, h('label', {}, '健診実施日'), f.examDate = h('input', { type: 'date', value: S.date }), h('label', {}, 'コース'), f.course = h('select', {}, ...S.masters.courses.map(c => h('option', { value: c.code }, c.name)))),
      h('div', { class: 'row' }, h('button', { class: 'btn p', onclick: async () => {
        try {
          const body = { id: p.id, name: f.name.value, kana: f.kana.value, sex: f.sex.value, birthDate: f.birthDate.value, falcoId: f.falcoId.value, chartNo: f.chartNo.value, employeeNo: f.employeeNo.value, office: f.office.value, dept: f.dept.value };
          const saved = await api('/api/patients', { method: 'POST', body });
          if (existing) { toast('保存しました'); await openExam(S.exam.exam.id); }
          else await createExamFor(saved.id, f.examDate.value, f.course.value);
        } catch (e) { toast(e.message, true); }
      } }, existing ? '保存' : '登録して健診回を作る'), existing ? null : h('button', { class: 'btn', onclick: () => renderMain() }, 'やめる'))));
  f.sex.value = p.sex || '男';
  return box;
}
async function createExamFor(patientId, examDate, course) {
  const e = await api('/api/exams', { method: 'POST', body: { patientId, examDate: examDate || S.date, course: course || S.masters.courses[0].code } });
  S.date = e.examDate; $('#date').value = S.date;
  await loadDay(); S.tab = 'measure'; await openExam(e.id);
}

// ---------- 健診回を開く ----------
async function openExam(id) {
  S.exam = await api('/api/exams/' + id);
  S.autoFocus = true;
  $$('#ptlist .pt').forEach(el => el.classList.remove('sel'));
  await loadDay();
  $('#side').classList.remove('open');
  renderMain();
}
function applyDto(dto) { S.exam = dto; renderJudgments(); const e = S.exams.find(x => x.id === dto.exam.id); if (e && e.status !== dto.exam.status) loadDay(); }

// ---------- 値の保存（項目ごとに即時）----------
let saveTimer = null;
function queueSave(key, value) {
  S.pendingSaves[key] = value;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSaves, 250);
}
async function flushSaves() {
  const body = S.pendingSaves; S.pendingSaves = {};
  if (Object.keys(body).length === 0 || !S.exam) return;
  try { applyDto(await api(`/api/exams/${S.exam.exam.id}/values`, { method: 'PUT', body })); } catch (e) { toast(e.message, true); }
}
const val = key => (S.exam && S.exam.values[key]) || '';

// ---------- 入力部品 ----------
function numInput(key, opts = {}) {
  const el = h('input', { type: 'text', inputmode: 'decimal', class: 'num ' + (opts.cls || ''), value: val(key), 'data-key': key, disabled: isLocked() ? '' : null, placeholder: opts.ph || '' });
  el.addEventListener('input', () => { check(el, opts); queueSave(key, el.value); });
  el.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); focusNext(el); } });
  el.addEventListener('focus', () => { S.focusKey = key; });
  check(el, opts);
  return el;
}
function check(el, opts) { const v = parseFloat(el.value); el.classList.toggle('bad', el.value !== '' && (isNaN(v) || (opts.min !== undefined && v < opts.min) || (opts.max !== undefined && v > opts.max))); }
const FOCUS_SEL = 'input[data-key]:not([disabled]), select[data-key]:not([disabled]), .radio[data-key][tabindex]';
function focusables() { return $$(FOCUS_SEL); }
function focusNext(el) { const all = focusables(); const i = all.indexOf(el); if (i >= 0 && i + 1 < all.length) all[i + 1].focus(); }
function focusPrev(el) { const all = focusables(); const i = all.indexOf(el); if (i > 0) all[i - 1].focus(); }
function radio(key, options, labels) {
  const g = h('span', { class: 'radio', 'data-key': key, 'data-radio': '1', tabindex: isLocked() ? null : '0' });
  options.forEach((o, i) => {
    const inp = h('input', { type: 'radio', name: key, value: o, disabled: isLocked() ? '' : null });
    if (val(key) === o) inp.checked = true;
    inp.addEventListener('change', () => queueSave(key, o));
    g.append(h('label', {}, inp, h('span', {}, labels ? labels[i] : o)));
  });
  g.addEventListener('click', () => { S.focusKey = key; });
  g.addEventListener('focus', () => { S.focusKey = key; });
  g.addEventListener('keydown', e => {
    if (isLocked()) return;
    const n = parseInt(e.key, 10);
    if (n >= 1 && n <= options.length) { e.preventDefault(); const inp = $$('input[type=radio]', g)[n - 1]; inp.checked = true; queueSave(key, options[n - 1]); focusNext(g); }
    else if (e.key === 'Enter' || e.key === 'ArrowDown') { e.preventDefault(); focusNext(g); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); focusPrev(g); }
  });
  return g;
}
function chk(key, label) {
  const inp = h('input', { type: 'checkbox', disabled: isLocked() ? '' : null });
  inp.checked = val(key) === '1';
  inp.addEventListener('change', () => queueSave(key, inp.checked ? '1' : ''));
  return h('label', { style: 'min-width:0;display:inline-flex;gap:6px;align-items:center' }, inp, label);
}
function selectInput(key, options, blank = true) {
  const s = h('select', { 'data-key': key, disabled: isLocked() ? '' : null }, blank ? h('option', { value: '' }, '—') : null, ...options.map(o => h('option', { value: typeof o === 'string' ? o : o.value }, typeof o === 'string' ? o : o.label)));
  s.value = val(key); s.addEventListener('change', () => queueSave(key, s.value));
  return s;
}
function textInput(key, ph) { const t = h('textarea', { rows: 3, style: 'width:100%', placeholder: ph || '', disabled: isLocked() ? '' : null }, val(key)); t.addEventListener('change', () => queueSave(key, t.value)); return t; }

// 判定の選択（手動 ↔ 自動）
function judgeBox(section) {
  const j = S.exam.judgments.find(x => x.section === section);
  const sel = h('select', { class: 'judgesel', 'data-section': section, disabled: isLocked() ? '' : null }, h('option', { value: '' }, '（未判定）'), ...codesFor(section).map(c => h('option', { value: c.code }, `${c.name}（${c.code}）`)));
  sel.value = j ? j.code : '';
  sel.addEventListener('change', async () => { try { applyDto(await api(`/api/exams/${S.exam.exam.id}/judgment`, { method: 'PUT', body: { section, code: sel.value || null } })); toast('判定を変更しました（手動）'); } catch (e) { toast(e.message, true); } });
  const wrap = h('span', { class: 'judge', 'data-judge': section }, '判定 ', sel, judgBadge(j),
    j && j.manual && !isLocked() ? h('button', { class: 'btn sm', onclick: async () => { applyDto(await api(`/api/exams/${S.exam.exam.id}/judgment`, { method: 'PUT', body: { section, code: null } })); } }, '自動に戻す') : null);
  return wrap;
}
function renderJudgments() {
  for (const el of $$('[data-judge]')) { const section = el.dataset.judge; const fresh = judgeBox(section); el.replaceWith(fresh); }
  for (const el of $$('[data-reason]')) { const j = S.exam.judgments.find(x => x.section === el.dataset.reason); el.textContent = j ? j.reason : ''; }
  const hdr = $('#statusBadge'); if (hdr) hdr.replaceWith(h('span', { id: 'statusBadge' }, statusBadge(S.exam.exam.status)));
  const c = $('#computed'); if (c) c.replaceWith(computedRow());
  if (S.tab === 'overall' || S.tab === 'blood' || S.tab === 'findings') renderMain(true);
}
function computedRow() {
  const c = S.exam.computed;
  return h('div', { class: 'row', id: 'computed' }, h('label', {}, 'BMI'), h('input', { class: 'num ro', readonly: '', value: c.bmi ?? '' }), h('span', { class: 'badge auto' }, '自動計算'),
    h('label', {}, '標準体重'), h('input', { class: 'num ro', readonly: '', value: c.standardWeight ?? '' }), h('span', { class: 'unit' }, 'kg'),
    h('label', {}, '肥満度'), h('input', { class: 'num ro', readonly: '', value: c.obesity == null ? '' : (c.obesity > 0 ? '+' : '') + c.obesity }), h('span', { class: 'unit' }, '%'));
}

// ---------- 画面本体 ----------
function renderMain(keepScroll) {
  const main = $('#main'); const sc = keepScroll ? ($('.content') || {}).scrollTop : 0;
  main.innerHTML = '';
  if (S.view !== 'day') { main.append(h('div', { class: 'content' }, viewOther())); return; }
  if (!S.exam) { main.append(h('div', { class: 'content' }, S.addingPatient ? patientDialog() : startCard())); return; }
  const p = S.exam.patient, e = S.exam.exam;
  main.append(h('div', { class: 'header' },
    h('span', { class: 'pname' }, p.name), h('span', { class: 'meta' }, `${p.sex} ／ ${p.birthDate}（${p.age ?? ''}歳）／ 個人ID ${p.falcoId || p.chartNo || '-'} ／ ${p.office || ''}`),
    h('span', { class: 'meta' }, `健診実施日 ${e.examDate} ／ ${e.course}`), h('span', { id: 'statusBadge' }, statusBadge(e.status)),
    h('span', { style: 'margin-left:auto' }, h('span', { class: 'badge auto' }, '自動＝プログラムが付けた判定'), ' ', h('span', { class: 'badge manual' }, '手動＝先生が変えた判定'))));
  const tabs = [['patient', '受診者'], ['measure', '計測・検査'], ['findings', '所見'], ['blood', '血液検査'], ['overall', '総合判定・印刷']];
  main.append(h('div', { class: 'tabs' }, ...tabs.map(([k, l]) => h('span', { class: 'tab' + (S.tab === k ? ' on' : ''), onclick: () => { S.tab = k; renderMain(); } }, l))));
  const content = h('div', { class: 'content' });
  content.append({ patient: tabPatient, measure: tabMeasure, findings: tabFindings, blood: tabBlood, overall: tabOverall }[S.tab]());
  main.append(content);
  if (S.autoFocus && S.tab === 'measure' && !keepScroll) { S.autoFocus = false; const q = $('input[data-key]:not([disabled])'); if (q) setTimeout(() => q.focus(), 0); }
  main.append(h('div', { class: 'footer' },
    h('button', { class: 'btn', onclick: () => openPdf('sheet') }, '入力シートを印刷'),
    h('button', { class: 'btn', onclick: () => openPdf('jushinhyo') }, '受診票を印刷'),
    h('button', { class: 'btn', onclick: () => openPdf('all') }, '3 種まとめて印刷'),
    h('button', { class: 'btn', onclick: () => { S.tab = 'overall'; renderMain(); } }, '総合判定へ'),
    h('span', { class: 'st' }, '最終保存 ' + e.updatedAt)));
  if (keepScroll) content.scrollTop = sc;
}

// ---- 受診者タブ ----
function tabPatient() {
  const e = S.exam.exam;
  return h('div', { class: 'cols' },
    h('div', { class: 'col', style: 'flex:1' }, patientDialog(S.exam.patient)),
    h('div', { class: 'col', style: 'flex:1' },
      h('div', { class: 'card' }, h('h4', {}, 'この健診回'), h('div', { class: 'b' },
        h('div', { class: 'row' }, h('label', {}, '健診実施日'), (() => { const i = h('input', { type: 'date', value: e.examDate, disabled: isLocked() ? '' : null }); i.addEventListener('change', async () => { applyDto(await api('/api/exams/' + e.id, { method: 'PUT', body: { examDate: i.value } })); S.date = i.value; $('#date').value = S.date; loadDay(); }); return i; })()),
        h('div', { class: 'row' }, h('label', {}, 'コース'), (() => { const s = h('select', { disabled: isLocked() ? '' : null }, ...S.masters.courses.map(c => h('option', { value: c.code }, c.name))); s.value = e.course; s.addEventListener('change', async () => { applyDto(await api('/api/exams/' + e.id, { method: 'PUT', body: { course: s.value } })); renderMain(); }); return s; })()),
        h('div', { class: 'row' }, h('label', {}, '実施医師名'), (() => { const i = h('input', { type: 'text', value: val('doctor_name'), placeholder: '空なら設定の医師名', disabled: isLocked() ? '' : null }); i.addEventListener('change', () => queueSave('doctor_name', i.value)); return i; })()),
        h('div', { class: 'row' }, h('label', {}, '状態'), statusBadge(e.status), e.confirmedAt ? h('span', { class: 'note' }, '確定 ' + e.confirmedAt) : null),
        h('div', { class: 'row' }, h('button', { class: 'btn', onclick: fillDefaults, disabled: isLocked() ? '' : null }, '既定値（正常）を入れる'), h('span', { class: 'note' }, '聴力「なし」、尿「−」、便「−」などを未入力の欄に入れます')),
        isLocked() ? null : h('div', { class: 'row' }, h('button', { class: 'btn', onclick: async () => { if (!confirm('この健診回を削除します。よろしいですか？')) return; await api('/api/exams/' + e.id, { method: 'DELETE' }); S.exam = null; await loadDay(); renderMain(); } }, 'この健診回を削除')))),
      h('div', { class: 'card' }, h('h4', {}, '過去の健診'), h('div', { class: 'b' }, S.exam.history.length ? h('table', {}, ...S.exam.history.map(x => h('tr', { class: 'click', onclick: () => openExam(x.id) }, h('td', {}, x.examDate), h('td', {}, x.course), h('td', {}, statusBadge(x.status))))) : h('span', { class: 'note' }, '前回の記録はありません')))));
}
async function fillDefaults() {
  try { const r = await api(`/api/exams/${S.exam.exam.id}/defaults`, { method: 'POST' }); applyDto(r.dto); renderMain(); toast(r.count ? `既定値を ${r.count} 欄に入れました` : '入れる欄がありません'); } catch (e) { toast(e.message, true); }
}

// ---- 計測タブ ----
function card(title, section, body, extraRight) {
  return h('div', { class: 'card', 'data-section': section }, h('h4', {}, title, h('span', { class: 'r' }, extraRight, section ? judgeBox(section) : null)), h('div', { class: 'b' }, body, section ? h('div', { class: 'note', 'data-reason': section }, (S.exam.judgments.find(x => x.section === section) || {}).reason || '') : null));
}
function sheetCameraCard() {
  const file = h('input', { type: 'file', accept: 'image/*', capture: 'environment', style: 'display:none' });
  const cam = h('button', { class: 'btn p', title: '紙の入力シートを撮って読み取る' }, '📷 入力シートを撮る');
  const prog = h('span', { class: 'note' });
  const handle = async blob => {
    cam.disabled = true; prog.textContent = '送信して読み取り中…（10〜20 秒）';
    try { await uploadPhoto(blob, 'sheet.jpg', S.exam ? S.exam.exam.id : null, f => { if (f.kind !== 'sheet') { if (!S.exam) { toast('入力シートではなく採血の報告書として読みました。受診者を開いて血液検査タブから取り込んでください', true); return; } toast('入力シートではなく採血の報告書として読みました。血液検査タブで確認してください'); S.tab = 'blood'; renderMain(); return; } sheetConfirm(f.fileName); }); }
    catch (e) { toast(e.message, true); } finally { cam.disabled = false; prog.textContent = ''; }
  };
  file.addEventListener('change', () => { if (file.files.length) handle(file.files[0]); file.value = ''; });
  cam.onclick = () => captureWithGuide('sheet', handle, file);
  return h('div', { class: 'card' }, h('h4', {}, '入力シートを撮って読み取る', h('span', { class: 'r note' }, '紙の入力シート（受診者ごとの印刷でも白紙でも）に書いて撮ると、数字と □ を読んで確認画面に出します')),
    h('div', { class: 'b row' }, cam, file, prog, h('span', { class: 'note' }, canUseCamera() ? 'カメラの映像にガイド枠が出ます' : 'http で開いているので端末のカメラアプリが開きます（ガイド枠を使うには https で開く）')));
}

// 受診者を開いていないときの入口：写真から始める（入力シートのマスで受診者を決める）か、白紙を印刷するか、「＋ 受診者」
function startCard() {
  return h('div', {},
    h('div', { class: 'note', style: 'padding:12px 20px 4px' }, '左の一覧から受診者を選ぶか、「＋ 受診者」で登録してください。写真から始めることもできます：'),
    sheetCameraCard(),
    h('div', { class: 'card' }, h('h4', {}, '白紙の入力シート'), h('div', { class: 'b row' },
      h('button', { class: 'btn', onclick: () => window.open('/api/sheet/blank', '_blank') }, '🖨 入力シート（白紙）を印刷'),
      h('span', { class: 'note' }, '受診者を登録しなくても使える。個人ID（ファルコの番号）・実施日・生年月日・性別をマスに書くと、写真から受診者が決まる（無ければその場で登録）'))));
}
function tabMeasure() {
  const secs = (S.exam.course?.sections) || [];
  const has = s => secs.includes(s) || secs.length === 0;
  const left = h('div', { class: 'col', style: 'flex:3' });
  if (!isLocked() && S.status?.ocr?.available) left.append(sheetCameraCard());
  if (has('計測')) left.append(card('計測', '計測', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '身長'), numInput('height', { min: 100, max: 220 }), h('span', { class: 'unit' }, 'cm'), h('label', {}, '体重'), numInput('weight', { min: 20, max: 200 }), h('span', { class: 'unit' }, 'kg'), h('label', {}, '腹囲'), numInput('waist', { min: 40, max: 160 }), h('span', { class: 'unit' }, 'cm')),
    computedRow())));
  if (has('視力')) left.append(card('視力', '視力', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '裸眼 右／左'), numInput('vision_r', { min: 0, max: 2, cls: 'sm' }), '／', numInput('vision_l', { min: 0, max: 2, cls: 'sm' }), h('label', {}, '矯正 右／左'), numInput('vision_rc', { min: 0, max: 2, cls: 'sm' }), '／', numInput('vision_lc', { min: 0, max: 2, cls: 'sm' })))));
  if (has('聴力')) left.append(card('聴力', '聴力', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '右 1000Hz'), radio('hear_r1000', ['なし', 'あり']), h('label', {}, '右 4000Hz'), radio('hear_r4000', ['なし', 'あり'])),
    h('div', { class: 'row' }, h('label', {}, '左 1000Hz'), radio('hear_l1000', ['なし', 'あり']), h('label', {}, '左 4000Hz'), radio('hear_l4000', ['なし', 'あり']), h('label', {}, '会話法'), selectInput('hear_conv', ['正常', '異常'])))));
  if (has('血圧')) left.append(card('血圧', '血圧', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '1 回目'), numInput('bp1_sys', { min: 50, max: 260 }), '／', numInput('bp1_dia', { min: 30, max: 160 }), h('span', { class: 'unit' }, 'mmHg'), h('label', {}, '2 回目'), numInput('bp2_sys', { min: 50, max: 260 }), '／', numInput('bp2_dia', { min: 30, max: 160 }), h('span', { class: 'unit' }, 'mmHg')),
    h('div', { class: 'row' }, h('label', {}, '降圧剤'), radio('bp_med', ['1', '0'], ['服用', 'なし'])))));
  if (has('尿検査')) left.append(card('尿検査', '尿検査', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '糖'), radio('urine_sugar', ['−', '±', '+', '2+', '3+']), h('label', {}, '蛋白'), radio('urine_protein', ['−', '±', '+', '2+', '3+'])),
    h('div', { class: 'row' }, h('label', {}, '潜血'), radio('urine_blood', ['−', '±', '+', '2+', '3+']), h('label', {}, 'ｳﾛﾋﾞﾘﾉｰｹﾞﾝ'), radio('urine_urobil', ['正常', '+', '−']), chk('urine_menstruating', '生理中')))));
  if (has('便検査')) left.append(card('便検査', '便検査', h('div', { class: 'row' }, h('label', {}, '潜血 1 回目'), radio('stool1', ['−', '+']), h('label', {}, '2 回目'), radio('stool2', ['−', '+']))));
  if (has('肺機能')) left.append(card('肺機能', '肺機能', h('div', {},
    h('div', { class: 'row' }, h('label', {}, '肺活量'), numInput('lung_vc'), h('span', { class: 'unit' }, 'ml'), h('label', {}, '予測肺活量'), numInput('lung_pred_vc'), h('span', { class: 'unit' }, 'ml'), h('label', {}, '肺活量比'), numInput('lung_vc_pct'), h('span', { class: 'unit' }, '%')),
    h('div', { class: 'row' }, h('label', {}, '努力肺活量'), numInput('lung_fvc'), h('span', { class: 'unit' }, 'ml'), h('label', {}, '1 秒量'), numInput('lung_fev1'), h('span', { class: 'unit' }, 'ml'), h('label', {}, '1 秒率'), numInput('lung_fev1_pct'), h('span', { class: 'unit' }, '%')))));
  left.append(card('食後時間', null, h('div', { class: 'row' }, h('label', {}, '食後'), numInput('meal_minutes', { min: 0, max: 3000 }), h('span', { class: 'unit' }, '分'))));
  const right = h('div', { class: 'col', style: 'flex:1.1' },
    h('div', { class: 'card' }, h('h4', {}, '問診（受診票の裏面）'), h('div', { class: 'b' },
      h('div', { class: 'row' }, chk('q_bp_med', '血圧を下げる薬')), h('div', { class: 'row' }, chk('q_dm_med', 'インスリン注射・血糖を下げる薬')), h('div', { class: 'row' }, chk('q_lipid_med', 'コレステロールを下げる薬')),
      h('div', { class: 'row' }, chk('q_smoking', 'タバコを吸っている')),
      h('div', { class: 'row' }, h('label', {}, '飲酒'), selectInput('q_alcohol_freq', ['毎日', '時々', 'ほとんど飲まない']), selectInput('q_alcohol_amount', ['1合未満', '1〜2合未満', '2〜3合未満', '3合以上'])),
      h('div', { class: 'row' }, chk('q_weight_gain10', '20 歳から 10kg 以上増加')), h('div', { class: 'row' }, chk('q_exercise', '運動を週 2 回以上している')),
      h('div', { class: 'row' }, h('label', {}, '既往歴'), textInput('q_history', '例：高血圧（治療中）')), h('div', { class: 'row' }, h('label', {}, '自覚症状'), textInput('q_symptoms')),
      h('div', { class: 'note' }, '喫煙→C017、BMI 25 以上→C018、飲酒が多い→C016 の候補に使います。'))),
    h('div', { class: 'card' }, h('h4', {}, 'この画面の判定基準'), h('div', { class: 'b note', style: 'line-height:1.7' }, criteriaText())));
  return h('div', { class: 'cols' }, left, right);
}
function criteriaText() {
  const c = S.masters.criteria; const sex = S.exam.patient.sex;
  const bmi = c.bmi.tiers.map(t => `${t.min ?? ''}〜${t.max ?? ''} → ${catName(t.code)}`).join('／');
  const bp = c.bloodPressure.tiers.map(t => `${t.sysMax ?? '∞'}/${t.diaMax ?? '∞'} 以下 → ${catName(t.code)}`).join('／');
  return [`BMI：${bmi}`, `腹囲：${sex === '女' ? c.waist.female : c.waist.male} 以上で ${catName(c.waist.code)}`, `血圧（${c.bloodPressure.use === 'second' ? '2 回目優先' : c.bloodPressure.use}）：${bp}`,
    `尿：（−）異常なし／（±）${catName(c.urine.protein['±'])}／（+）${catName(c.urine.protein['+'])}`, `視力：矯正含め ${c.vision.normalMin} 以上で異常なし`, `便：1 回でも（+）なら ${catName(c.stool.code)}`, '数値は config/判定基準.json で変更できます。「仮」はマスター入手後に直します。'].map(t => h('div', {}, t));
}

// ---- 所見タブ ----
function tabFindings() {
  const secs = (S.exam.course?.sections) || [];
  const left = h('div', { class: 'col', style: 'flex:3' });
  const picker = h('div', { class: 'col', style: 'flex:1.3' });
  for (const section of S.masters.findingSections) {
    if (secs.length && !secs.includes(section) && !S.exam.findings.some(f => f.section === section)) continue;
    const fs = S.exam.findings.filter(f => f.section === section);
    const dateKey = 'date:' + section;
    const di = h('input', { type: 'date', value: val(dateKey), disabled: isLocked() ? '' : null }); di.addEventListener('change', () => queueSave(dateKey, di.value));
    const extra = section === '胸部X線' ? radio('xray_direct', ['直接', '間接']) : section === '心電図' ? chk('ecg_load', '負荷') : null;
    const body = h('div', {},
      h('div', { class: 'row' }, h('label', {}, '受診日'), di, extra),
      h('div', { class: 'row' }, h('label', {}, '所見'), ...fs.map(f => h('span', { class: 'chip' }, (f.code ? f.code + ' ' : '') + f.text, isLocked() ? null : h('b', { onclick: async () => { applyDto(await api(`/api/exams/${S.exam.exam.id}/findings/${f.id}`, { method: 'DELETE' })); renderMain(true); } }, '✕'))),
        isLocked() ? null : h('button', { class: 'btn sm', onclick: () => showPicker(picker, section) }, '＋ 所見を追加')),
      section === '胃内視鏡' ? h('div', { class: 'row' }, h('label', {}, '次回の胃カメラ'), selectInput('next_gastro_years', ['1', '2', '3', '5']), '年後', h('span', { class: 'note' }, '「検査内容の説明」の末尾に印字')) : null,
      section === '大腸内視鏡' ? h('div', { class: 'row' }, h('label', {}, '次回の大腸カメラ'), selectInput('next_colon_years', ['1', '2', '3', '5']), '年後') : null);
    left.append(card(section, section, body));
  }
  if (!isLocked()) left.append(h('div', { class: 'card' }, h('h4', {}, 'その他の検査'), h('div', { class: 'b row' }, ...S.masters.findingSections.filter(s => !(secs.includes(s)) && !S.exam.findings.some(f => f.section === s)).map(s => h('button', { class: 'btn sm', onclick: () => showPicker(picker, s) }, '＋ ' + s)))));
  picker.append(h('div', { class: 'card' }, h('h4', {}, '所見を追加'), h('div', { class: 'b note' }, '左の「＋ 所見を追加」を押すと、ここに一覧が出ます。')),
    h('div', { class: 'card' }, h('h4', {}, '所見から判定を決める規則'), h('div', { class: 'b note' }, '所見マスター（実物）には判定が付いていないので、名称が「異常なし」「所見なし」などなら 異常なし（150）、それ以外の所見があれば仮に 要経過観察（180）を付けます。先生が各部位の判定で変えてください。対応を決めたい所見は config/所見マスター.csv の judgment 列に書けます。'), h('div', { class: 'b', style: 'display:none' }, h('table', {}, h('tr', {}, h('th', {}, '所見'), h('th', {}, '判定')), ...S.masters.findings.filter(f => f.judgment !== '150').slice(0, 12).map(f => h('tr', {}, h('td', {}, `${f.section}：${f.text}`), h('td', {}, catName(f.judgment))))), h('div', { class: 'note' }, '複数の所見があれば最も重い判定を案にします。config/所見マスター.csv で変更できます。'))));
  return h('div', { class: 'cols' }, left, picker);
}
function showPicker(picker, section) {
  const list = h('div', { class: 'list' });
  const q = h('input', { type: 'text', placeholder: 'コードか名称で検索（例：ポリープ、U004）', style: 'width:100%' });
  const free = h('input', { type: 'text', placeholder: '自由記述', style: 'width:100%' });
  const count = h('div', { class: 'note' });
  const add = async (code, text) => { try { applyDto(await api(`/api/exams/${S.exam.exam.id}/findings`, { method: 'POST', body: { section, code, text } })); renderMain(true); showPicker($$('.col')[1], section); } catch (e) { toast(e.message, true); } };
  const render = () => {
    list.innerHTML = ''; const t = q.value.trim();
    const all = S.masters.findings.filter(f => f.section === section && (!t || f.code.includes(t) || f.text.includes(t)));
    const shown = all.slice(0, 150);
    for (const f of shown) list.append(h('div', { onclick: () => add(f.code, null) }, `${f.code}　${f.text}${f.note && f.note.startsWith('仮') ? '（仮）' : ''}${f.judgment ? '　→ ' + catName(f.judgment) : ''}`));
    count.textContent = all.length > shown.length ? `${all.length} 件のうち ${shown.length} 件を表示。検索で絞ってください` : `${all.length} 件`;
  };
  q.addEventListener('input', render); render();
  picker.innerHTML = '';
  // 「所見なし」はコード無しの所見として入れる（判定は異常なし）
  const noneBtn = h('button', { class: 'btn', onclick: () => add(null, '所見なし') }, '所見なし（異常なし）で入れる');
  // 胸部X線：位置＋部位＋所見の組み合わせコード（例 A001＝右肺野異常陰影）
  let compose = null;
  if (section === '胸部X線' && S.masters.xrayParts && S.masters.xrayParts['所見']) {
    const sel = kind => h('select', {}, ...S.masters.xrayParts[kind].map(p => h('option', { value: p.code }, `${p.code} ${p.name}`)));
    const pos = sel('位置'), part = sel('部位'), fnd = sel('所見');
    const fq = h('input', { type: 'text', placeholder: '所見を検索', style: 'width:140px' });
    fq.addEventListener('input', () => { const t = fq.value.trim(); fnd.innerHTML = ''; for (const p of S.masters.xrayParts['所見'].filter(p => !t || p.name.includes(t) || p.code.includes(t))) fnd.append(h('option', { value: p.code }, `${p.code} ${p.name}`)); });
    compose = h('div', { class: 'row', style: 'margin-top:6px' }, h('span', { class: 'note' }, '組み合わせ：'), pos, part, fq, fnd, h('button', { class: 'btn sm p', onclick: () => add(pos.value + part.value + fnd.value, null) }, '組み合わせで追加'));
  }
  picker.append(h('div', { class: 'card' }, h('h4', {}, '所見を追加 ― ' + section), h('div', { class: 'b' }, h('div', { class: 'row' }, noneBtn), compose, h('div', { class: 'row' }, q), count, list, h('div', { class: 'row', style: 'margin-top:8px' }, free, h('button', { class: 'btn', onclick: () => { if (free.value.trim()) add(null, free.value.trim()); } }, '自由記述で追加')))));
  q.focus();
}

// ---- 血液タブ ----
function tabBlood() {
  const x = S.exam;
  const left = h('div', { class: 'col', style: 'flex:2' });
  const importCard = h('div', { class: 'card' }, h('h4', {}, '取り込み元', h('span', { class: 'r' }, h('button', { class: 'btn', onclick: () => loadImport(importCard, true) }, '取込フォルダを読み直す'))), h('div', { class: 'b' }, h('div', { class: 'note' }, '読み込み中…')));
  left.append(importCard); loadImport(importCard, false);
  // 枠はマスター（config/血液項目.csv）の全項目。値の無い枠は空欄。項目の少ない健診でも枠は同じで、数字を入れない
  const masters = S.masters.bloodItems || [];
  const sex = x.patient.sex;
  const refOf = m => (sex === '女' && m.refFemale) ? m.refFemale : m.refMale;
  const used = new Set();
  const saveRow = async (item, inp, unit, refText) => {
    try {
      const dto = await api(`/api/exams/${x.exam.id}/blood`, { method: 'PUT', body: { item, value: inp.value.trim(), unit, refText } });
      // 保存中に Enter で次の欄へ移っている。描き直したあと同じ欄へ戻す
      const focusItem = document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.blood : null;
      applyDto(dto); renderMain(true);
      if (focusItem) { const el = $(`input[data-blood="${focusItem}"]`); if (el) el.focus(); }
    } catch (e) { toast(e.message, true); }
  };
  const row = (m, b) => {
    const refText = (b && b.refText) || (m ? refOf(m) : '');
    const unit = (b && b.unit) || (m ? m.unit : '');
    const item = m ? m.item : b.item;
    const inp = h('input', { type: 'text', class: 'num', value: b ? b.value : '', 'data-blood': item, disabled: isLocked() ? '' : null, inputmode: m && m.kind === '定性' ? 'text' : 'decimal', placeholder: m && m.kind === '定性' ? '陰性／陽性' : '' });
    inp.addEventListener('change', () => { if ((b ? b.value : '') !== inp.value.trim()) saveRow(item, inp, unit, refText); });
    inp.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === 'ArrowDown') { e.preventDefault(); const all = $$('input[data-blood]:not([disabled])'); const i = all.indexOf(inp); if (i + 1 < all.length) all[i + 1].focus(); } else if (e.key === 'ArrowUp') { e.preventDefault(); const all = $$('input[data-blood]:not([disabled])'); const i = all.indexOf(inp); if (i > 0) all[i - 1].focus(); } });
    return h('tr', { class: b ? '' : 'empty' }, h('td', {}, item, m && m.note ? h('span', { class: 'note', style: 'margin-left:6px' }, m.note) : null), h('td', { class: 'num' }, inp), h('td', {}, unit), h('td', {}, refText),
      h('td', { class: b && b.flag === '↑' ? 'hi' : b && b.flag === '↓' ? 'lo' : '' }, b ? b.flag : ''),
      h('td', {}, b && !isLocked() ? h('button', { class: 'btn sm', title: 'この値を消す', onclick: async () => { applyDto(await api(`/api/exams/${x.exam.id}/blood/${b.id}`, { method: 'DELETE' })); renderMain(true); } }, '消す') : null));
  };
  const rows = [];
  const cats = [...new Set(masters.map(m => m.category))];
  for (const cat of cats) {
    rows.push(h('tr', { class: 'grp' }, h('td', { colspan: 6 }, cat)));
    for (const m of masters.filter(m => m.category === cat)) { const b = x.blood.find(b => b.item === m.item); if (b) used.add(b.id); rows.push(row(m, b)); }
  }
  const extra = x.blood.filter(b => !used.has(b.id));
  if (extra.length) { rows.push(h('tr', { class: 'grp' }, h('td', { colspan: 6 }, 'マスターに無い項目（取り込んだまま）'))); for (const b of extra) rows.push(row(null, b)); }
  const add = { item: h('input', { type: 'text', placeholder: '項目名', style: 'width:150px' }), value: h('input', { type: 'text', class: 'num', placeholder: '結果' }), unit: h('input', { type: 'text', class: 'sm', placeholder: '単位' }), ref: h('input', { type: 'text', placeholder: '基準値（例 39~98、5.0以下）', style: 'width:150px' }) };
  left.append(h('div', { class: 'card' }, h('h4', {}, `血液検査の値（入力 ${x.blood.length}／枠 ${masters.length} 項目）`, h('span', { class: 'r note' }, '↑↓ は基準値外。値の無い枠は空欄のまま（印字も空欄）。欄に直接打てる。Enter で次の欄')),
    h('div', { class: 'b', style: 'padding:0' }, h('table', { class: 'blood' }, h('tr', {}, h('th', {}, '項目'), h('th', { style: 'text-align:right' }, '結果'), h('th', {}, '単位'), h('th', {}, '基準値'), h('th', {}, ''), h('th', {}, '')), ...rows)),
    isLocked() ? null : h('div', { class: 'b row' }, h('span', { class: 'note' }, 'マスターに無い項目を足す：'), add.item, add.value, add.unit, add.ref, h('button', { class: 'btn sm', onclick: async () => { try { applyDto(await api(`/api/exams/${x.exam.id}/blood`, { method: 'PUT', body: { item: add.item.value, value: add.value.value, unit: add.unit.value, refText: add.ref.value } })); renderMain(true); } catch (e) { toast(e.message, true); } } }, '追加・更新'))));
  const right = h('div', { class: 'col', style: 'flex:1.3;min-width:300px' },
    h('div', { class: 'card' }, h('h4', {}, '血液検査判定（分野ごと）'), h('div', { class: 'b' }, ...S.masters.bloodCategories.map(cat => { const sec = '血液:' + cat; const j = x.judgments.find(k => k.section === sec); const has = x.blood.some(b => b.category === cat); return h('div', { style: 'border-bottom:1px solid #eee;padding:6px 0' }, h('div', { class: 'row', style: 'margin:0' }, h('label', { style: 'min-width:70px;font-weight:bold' }, cat), has ? judgeBox(sec) : h('span', { class: 'note' }, '（項目なし）')), j && j.reason ? h('div', { class: 'note', 'data-reason': sec }, j.reason) : null); }), h('div', { class: 'note', style: 'margin-top:6px' }, '規則：分野内に基準値外が 1 つでもあれば要経過観察（仮）。大きく外れたときの閾値は config/判定基準.json の severe。'))),
    h('div', { class: 'card' }, h('h4', {}, 'メタボリックシンドローム判定'), h('div', { class: 'b' }, h('div', { class: 'row' }, h('span', { style: 'font-size:18px;font-weight:bold' }, x.metabolic.label), h('span', { class: 'badge auto' }, '自動')), h('div', { class: 'note' }, x.metabolic.detail), h('div', { class: 'note', style: 'margin-top:6px' }, x.metabolic.comment))));
  return h('div', { class: 'cols' }, left, right);
}
async function loadImport(cardEl, rescan) {
  try {
    const imp = await api('/api/import/files');
    const body = cardEl.querySelector('.b'); body.innerHTML = '';
    // 撮影・写真の選択（携帯・タブレットではカメラが起動する。PC ではファイルを選ぶ）
    const file = h('input', { type: 'file', accept: 'image/*,.pdf', capture: 'environment', multiple: '', style: 'display:none' });
    const camBtn = h('button', { class: 'btn p', disabled: isLocked() ? '' : null, onclick: () => captureWithGuide('falco', async blob => { camBtn.disabled = true; prog.textContent = '送信して読み取り中…（10〜20 秒）'; try { await uploadPhoto(blob, 'falco.jpg', null, async f => { toast('読み取りました'); await loadImport(cardEl, true); if (f.kind === 'sheet') { S.tab = 'measure'; renderMain(); sheetConfirm(f.fileName); } else photoConfirm(f.fileName); }); } catch (e) { toast(e.message, true); camBtn.disabled = false; prog.textContent = ''; } }, file) }, '📷 報告書を撮る');
    const pickBtn = h('button', { class: 'btn', disabled: isLocked() ? '' : null, onclick: () => file.click() }, '写真・PDF を選ぶ');
    const prog = h('span', { class: 'note' });
    file.addEventListener('change', async () => {
      if (!file.files.length) return;
      const fd = new FormData();
      for (const f of file.files) fd.append('files', f, f.name);
      camBtn.disabled = true; prog.textContent = `送信して読み取り中…（${file.files.length} 枚。1 枚 10〜20 秒）`;
      try {
        const r = await fetch('/api/import/upload', { method: 'POST', body: fd });
        const j = await r.json(); if (!r.ok) throw new Error(j.error || r.statusText);
        const errs = j.files.filter(x => x.error);
        toast(errs.length ? `${j.files.length - errs.length} 枚を読み取り、${errs.length} 枚は読めませんでした` : `${j.files.length} 枚を読み取りました`, errs.length > 0);
        await loadImport(cardEl, true);
        const first = j.files.find(x => !x.error);
        if (first) photoConfirm(first.fileName);
      } catch (e) { toast(e.message, true); camBtn.disabled = false; prog.textContent = ''; }
    });
    body.append(h('div', { class: 'row' }, camBtn, pickBtn, file, prog, h('span', { class: 'note' }, S.status?.ocr?.available ? '写真は PC の中で読み取り、看護師が確認してから保存します（外部送信なし）' : '写真の読み取りは使えません：' + (S.status?.ocr?.status || ''))));
    body.append(h('div', { class: 'row' }, h('label', {}, '取込フォルダ'), h('span', {}, imp.dir), h('span', { class: 'note' }, '写真（jpg/png）か、文字の入った PDF・CSV を置く')));
    if (imp.files.length === 0) { body.append(h('div', { class: 'note' }, 'ファイルがありません')); return; }
    body.append(h('table', {}, h('tr', {}, h('th', {}, 'ファイル'), h('th', {}, '採取日'), h('th', {}, '読み取った氏名・番号'), h('th', {}, '突合'), h('th', {}, '読み取り'), h('th', {}, '')),
      ...imp.files.map(f => h('tr', {}, h('td', {}, (f.kind === 'sheet' ? '📝 ' : f.kind === 'photo' ? '📷 ' : '📄 ') + f.fileName + (f.page ? `（${f.page}）` : '')), h('td', {}, f.takenAt), h('td', {}, f.kind === 'sheet' ? (f.patientHint || '入力シート') : (f.nameHint || '') + (f.idHint ? ' ／ ' + f.idHint : '')),
        h('td', {}, f.imported ? h('span', { class: 'badge done' }, f.imported) : h('span', { class: 'badge ' + (f.match === '一致' ? 'done' : 'wait') }, f.match + (f.suggestedLabel ? '：' + f.suggestedLabel : ''))),
        h('td', {}, f.error ? h('span', { class: 'err' }, f.error) : (f.kind === 'photo' || f.kind === 'sheet') ? (f.read ? h('span', {}, `${f.itemCount} 項目（`, h('span', { class: 'ok' }, `確定 ${f.okCount}`), '／', h('span', { class: f.checkCount ? 'err' : '' }, `要確認 ${f.checkCount}`), '）') : '未読取') : f.itemCount + ' 項目'),
        h('td', {}, isLocked() ? null : f.kind === 'sheet'
          ? h('button', { class: 'btn sm p', onclick: async () => { if (f.suggestedExamId && f.suggestedExamId !== S.exam.exam.id) { if (!confirm(`この入力シートは別の健診回（${f.suggestedLabel}）のものです。その受診者を開いて確認しますか？`)) return; S.tab = 'measure'; await openExam(f.suggestedExamId); } else { S.tab = 'measure'; renderMain(); } sheetConfirm(f.fileName); } }, f.read && !f.error ? '確認して取り込む' : '読み取る')
          : f.kind === 'photo'
          ? h('button', { class: 'btn sm p', onclick: () => photoConfirm(f.fileName) }, f.read && !f.error ? '確認して取り込む' : '読み取る')
          : f.error ? null : h('button', { class: 'btn sm p', onclick: async () => { if (!confirm(`「${f.fileName}」を ${S.exam.patient.name} の血液検査として取り込みます。よろしいですか？`)) return; try { const r = await api('/api/import/apply', { method: 'POST', body: { file: f.fileName, examId: S.exam.exam.id } }); toast(r.message + `（${r.count} 項目）`); applyDto(r.dto); renderMain(true); } catch (e) { toast(e.message, true); } } }, 'この受診者に取り込む'))))));
  } catch (e) { cardEl.querySelector('.b').textContent = e.message; }
}

// ---- 撮影アシスト ----
// https で開いているときはブラウザのカメラを使い、用紙のガイド枠を重ねて撮る（撮った写真は PC へ送るだけで端末に残らない）。
// http（カメラが使えない）のときは端末のカメラアプリを開く（input type=file）。
function canUseCamera() { return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.isSecureContext); }
async function captureWithGuide(kind, onBlob, fallbackInput) {
  if (!canUseCamera()) { fallbackInput.click(); return; }
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' }, width: { ideal: 3000 }, height: { ideal: 4000 } }, audio: false }); }
  catch (e) { toast('カメラが使えないので端末のカメラアプリを開きます', true); fallbackInput.click(); return; }
  const video = h('video', { autoplay: '', playsinline: '', muted: '', style: 'width:100%;max-height:70vh;background:#000;object-fit:contain' });
  video.srcObject = stream;
  const guide = h('div', { class: 'camguide' }, h('div', { class: 'frame' }), h('div', { class: 'hint' }, kind === 'sheet' ? '四隅の黒い四角が枠の角に来るように。影を入れず真上から' : '報告書の全体（左右 2 枚の表）が枠に入るように。影を入れず真上から'));
  const shot = h('button', { class: 'btn p', style: 'font-size:18px;padding:10px 24px' }, '📷 撮る');
  const cancel = h('button', { class: 'btn' }, 'やめる');
  const stat = h('div', { class: 'note', style: 'color:#fff' }, '');
  const box = h('div', { class: 'cammodal' }, h('div', { class: 'camwrap' }, video, guide), h('div', { class: 'row', style: 'justify-content:center;margin-top:8px' }, shot, cancel), stat);
  document.body.append(box);
  const close = () => { stream.getTracks().forEach(t => t.stop()); box.remove(); };
  cancel.onclick = close;
  shot.onclick = () => {
    const w = video.videoWidth, hh = video.videoHeight; if (!w) return;
    const cv = document.createElement('canvas'); cv.width = w; cv.height = hh; cv.getContext('2d').drawImage(video, 0, 0);
    // ぼけの簡易チェック（縮小した画像の隣接画素の差）。差が小さすぎたら注意
    const sm = document.createElement('canvas'); sm.width = 400; sm.height = Math.round(400 * hh / w); const g = sm.getContext('2d'); g.drawImage(cv, 0, 0, sm.width, sm.height);
    const d = g.getImageData(0, 0, sm.width, sm.height).data; let acc = 0, n = 0, bright = 0;
    for (let y = 0; y < sm.height; y += 2) for (let x = 1; x < sm.width; x += 2) { const i = (y * sm.width + x) * 4; const j = i - 4; acc += Math.abs(d[i] - d[j]); bright += d[i]; n++; }
    const sharp = acc / n, mean = bright / n;
    if (mean < 60) { stat.textContent = '暗すぎます。明るい所で撮り直してください'; return; }
    if (sharp < 4) { stat.textContent = 'ぼけているようです。ピントを合わせて撮り直してください'; return; }
    cv.toBlob(blob => { close(); onBlob(blob); }, 'image/jpeg', 0.92);
  };
}
async function uploadPhoto(blob, name, examId, onDone) {
  const fd = new FormData(); fd.append('files', blob, name); if (examId) fd.append('examId', examId);
  const r = await fetch('/api/import/upload', { method: 'POST', body: fd });
  const j = await r.json(); if (!r.ok) throw new Error(j.error || r.statusText);
  const f = j.files[0]; if (f.error) throw new Error(f.error);
  onDone(f);
}

// ---- 入力シートの読み取り結果の確認画面 ----
// □ は墨の量だけで判定し、数字は 1 マスずつ読む。読めた値を欄に入れて見せ、人が OK してから保存する。
async function sheetConfirm(fileName, reread) {
  let x = S.exam;
  let host = $('#sheetConfirm');
  if (!host) { host = h('div', { id: 'sheetConfirm' }); const first = $('.content .col .card') || $('.content .card'); if (first) first.before(host); else $('.content').prepend(host); }
  host.innerHTML = ''; host.append(h('div', { class: 'card' }, h('h4', {}, '入力シートの読み取り結果'), h('div', { class: 'b note' }, '読み取り中…')));
  host.scrollIntoView({ block: 'start' });
  let d;
  try { d = await api('/api/import/sheet?file=' + encodeURIComponent(fileName) + (x ? '&examId=' + x.exam.id : '') + (reread ? '&reread=true' : '')); } catch (e) { host.innerHTML = ''; host.append(h('div', { class: 'card' }, h('div', { class: 'b err' }, e.message))); return; }
  const r = d.result;
  // 受診者のマスから別の（または未選択で）健診回に決まったら、その受診者を開いてから確認画面を出す
  if (r.examId && (!x || r.examId !== x.exam.id) && !r.error) { S.tab = 'measure'; await openExam(r.examId); toast(`受診者のマスから ${d.examLabel || '受診者'} と決めました`); return sheetConfirm(fileName); }
  host = $('#sheetConfirm') || host; host.innerHTML = '';
  if (!x && !r.examId && !r.error) return sheetConfirmNew(fileName, d, host);
  if (r.error) { host.append(h('div', { class: 'card' }, h('h4', {}, '入力シートの読み取り結果'), h('div', { class: 'b' }, h('div', { class: 'err' }, r.error), h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => sheetConfirm(fileName, true) }, '読み直す'), h('button', { class: 'btn', onclick: () => host.remove() }, '閉じる'))))); return; }
  const same = !r.examId || r.examId === x.exam.id;
  const hdr = d.header || {};
  const hdrText = ['_falco_id', '_birth', '_sex', '_date'].filter(k => hdr[k]).map(k => ({ _falco_id: '個人ID ', _birth: '生 ', _sex: '', _date: '実施日 ' })[k] + hdr[k]).join(' ／ ');
  // キーごとにまとめる（check は選択肢を 1 行に）
  const groups = new Map();
  for (const f of d.fields) { if (!groups.has(f.key)) groups.set(f.key, { key: f.key, label: f.label, kind: f.kind, items: [] }); groups.get(f.key).items.push(f); }
  const inputs = {};
  const rows = [];
  for (const g of groups.values()) {
    if (g.key.startsWith('finding') || g.key.startsWith('_')) continue; // 受診者のマスは上の帯に出す（値としては保存しない）
    if (g.kind === 'digits') {
      const f = g.items[0]; if (f.status === 'empty') continue;
      const inp = h('input', { type: 'text', class: 'num', value: f.value || '', 'data-sheet-key': g.key, inputmode: 'decimal' });
      inputs[g.key] = () => inp.value.trim();
      rows.push(h('tr', { class: 'ph ' + (f.status === 'ok' ? 'ok' : 'chk') }, h('td', {}, h('span', { class: 'badge ' + (f.status === 'ok' ? 'auto' : 'wait') }, f.status === 'ok' ? '読めた' : '要確認')), h('td', {}, g.label), h('td', { class: 'num' }, inp),
        h('td', {}, f.crop ? h('img', { src: f.crop, class: 'crop' }) : h('span', { class: 'note' }, (f.cellTexts || []).join(' '))), h('td', { class: 'note' }, 'マス：' + (f.cellTexts || []).map(t => t || '＿').join(' ')), h('td', { class: f.status === 'ok' ? 'note' : 'err' }, f.reason || '')));
    } else {
      const filled = g.items.filter(i => i.status === 'ok'); const faint = g.items.filter(i => i.status === 'check');
      if (filled.length === 0 && faint.length === 0) continue;
      const sel = h('select', { 'data-sheet-key': g.key }, h('option', { value: '' }, '（入れない）'), ...g.items.map(i => h('option', { value: i.option, selected: filled.length === 1 && filled[0].option === i.option ? '' : null }, i.option)));
      inputs[g.key] = () => sel.value;
      const st = filled.length === 1 && faint.length === 0 ? 'ok' : 'chk';
      rows.push(h('tr', { class: 'ph ' + st }, h('td', {}, h('span', { class: 'badge ' + (st === 'ok' ? 'auto' : 'wait') }, st === 'ok' ? '読めた' : '要確認')), h('td', {}, g.label), h('td', {}, sel),
        h('td', { class: 'note' }, g.items.map(i => `${i.option}:${Math.round((i.cellInk && i.cellInk[0] || 0) * 100)}%`).join(' ')), h('td', {}, ''), h('td', { class: st === 'ok' ? 'note' : 'err' }, st === 'ok' ? '' : (faint.map(i => i.reason).find(Boolean) || '2 つ以上塗られている'))));
    }
  }
  const findings = (d.findings || []).map(f => ({ ...f, on: true }));
  const fRows = findings.map(f => { const cb = h('input', { type: 'checkbox', checked: '' }); cb.addEventListener('change', () => { f.on = cb.checked; }); return h('tr', { class: 'ph ok' }, h('td', {}, cb), h('td', {}, '所見 ' + f.section), h('td', { colspan: 4 }, `${f.code} ${f.text}`)); });
  const checkRows = (d.fields || []).filter(f => f.key.startsWith('finding_code') && f.status === 'check').map(f => h('tr', { class: 'ph chk' }, h('td', {}, h('span', { class: 'badge wait' }, '要確認')), h('td', {}, f.label), h('td', { colspan: 4, class: 'err' }, `${f.value}：${f.reason}（手で所見タブから入れてください）`)));
  const nCheck = rows.filter(tr => tr.classList.contains('chk')).length + checkRows.length;
  const save = async () => {
    const values = {}; for (const [k, get] of Object.entries(inputs)) { const v = get(); if (v !== '') values[k] = v; }
    const fs = findings.filter(f => f.on).map(f => ({ section: f.section, code: f.code }));
    if (!same && !confirm(`このシートは健診回 ${r.examId} のものと読めましたが、いまの受診者は健診回 ${x.exam.id} です。それでも入れますか？`)) return;
    if (!confirm(`${Object.keys(values).length} 項目と所見 ${fs.length} 件を ${x.patient.name} に保存します。要確認の項目は紙と見比べましたか？`)) return;
    try { const res = await api('/api/import/apply-sheet', { method: 'POST', body: { file: fileName, examId: x.exam.id, values, findings: fs } }); toast(res.message + `（${res.count} 件）`); applyDto(res.dto); host.remove(); renderMain(); } catch (e) { toast(e.message, true); }
  };
  host.append(h('div', { class: 'card' },
    h('h4', {}, `入力シートの読み取り結果：${fileName}`, h('span', { class: 'r note' }, `${rows.length + fRows.length} 件（要確認 ${nCheck}）`)),
    h('div', { class: 'b' },
      h('div', { class: 'row' }, h('span', { class: 'badge ' + (same ? 'done' : 'wait') }, r.examId ? (same ? `健診回 ${r.examId} のシート（この受診者）` : `健診回 ${r.examId} のシート：${d.examLabel || '別の受診者'}`) : '健診回の番号が読めず（この受診者のシートとして扱う）'),
        hdrText ? h('span', { class: 'note' }, '受診者のマス：' + hdrText) : null,
        h('a', { href: '/api/import/image?file=' + encodeURIComponent(fileName), target: '_blank', class: 'btn sm' }, '写真を見る'), h('span', { class: 'note' }, (r.notes || []).join('。'))),
      h('div', { style: 'padding:0' }, h('table', { class: 'photo' }, h('tr', {}, h('th', {}, '状態'), h('th', {}, '項目'), h('th', {}, '値（直せる）'), h('th', {}, '写真'), h('th', {}, '読み'), h('th', {}, '')), ...rows, ...fRows, ...checkRows)),
      h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { class: 'btn p', onclick: save }, 'この内容で保存'), h('button', { class: 'btn', onclick: () => sheetConfirm(fileName, true) }, '読み直す'), h('button', { class: 'btn', onclick: () => host.remove() }, 'やめる')))));
}

// 受診者を開いておらず、受診者のマスからも既存の健診回に決まらなかったとき：受診者（既存に足す／新しく登録）と実施日・コースを決めてから、同じ確認画面へ
function sheetConfirmNew(fileName, d, host) {
  const r = d.result, sg = d.suggest || {}, hdr = d.header || {};
  const f = {};
  const field = (label, key, value, ph) => { f[key] = h('input', { type: 'text', value: value || '', placeholder: ph || '' }); return h('div', { class: 'row' }, h('label', {}, label), f[key]); };
  const existing = sg.patientId ? h('div', { class: 'row' }, h('span', { class: 'badge done' }, `既存の受診者：${sg.patientLabel}`), h('span', { class: 'note' }, sg.note || '')) : null;
  const cands = (sg.candidates || []).length ? h('div', { class: 'row' }, h('label', {}, '候補'), (() => { const sel = h('select', {}, h('option', { value: '' }, '（新しく登録する）'), ...sg.candidates.map(c => h('option', { value: c.id }, c.label))); f.candidate = sel; return sel; })(), h('span', { class: 'note' }, sg.note || '')) : null;
  const newForm = sg.patientId ? null : h('div', {},
    h('div', { class: 'note' }, (sg.note || '一致する受診者がいない') + '。読み取ったマスの値を入れてあります。氏名を書いて登録してください。'),
    field('氏名 *', 'name', '', '例：健診 花子'), field('カナ', 'kana', ''),
    h('div', { class: 'row' }, h('label', {}, '性別'), (f.sex = h('select', {}, ...['', '男', '女'].map(v => h('option', { value: v, selected: v === (hdr._sex || '') ? '' : null }, v || '—'))))),
    field('生年月日', 'birthDate', hdr._birth, '1974-05-01'), field('個人ID（ファルコ）', 'falcoId', hdr._falco_id));
  const examRow = h('div', { class: 'row' }, h('label', {}, '健診実施日'), (f.examDate = h('input', { type: 'date', value: hdr._date || S.date })), h('label', {}, 'コース'), (f.course = h('select', {}, ...S.masters.courses.map(c => h('option', { value: c.code }, c.name)))));
  const go = async () => {
    const pid = sg.patientId || (f.candidate && f.candidate.value ? Number(f.candidate.value) : null);
    const create = { patientId: pid, examDate: f.examDate.value, course: f.course.value };
    if (!pid) { if (!f.name.value.trim()) { toast('氏名を入れてください', true); return; } Object.assign(create, { name: f.name.value, kana: f.kana.value, sex: f.sex.value, birthDate: f.birthDate.value, falcoId: f.falcoId.value }); }
    // 受診者と健診回だけ先に作り（値はまだ入れない）、その健診回を開いて普通の確認画面へ
    try {
      const res = await api('/api/import/apply-sheet', { method: 'POST', body: { file: fileName, examId: 0, values: {}, findings: [], create, dryRun: true } });
      S.date = res.dto.exam.examDate; $('#date').value = S.date; await loadDay(); S.tab = 'measure'; await openExam(res.examId); sheetConfirm(fileName);
    } catch (e) { toast(e.message, true); }
  };
  host.append(h('div', { class: 'card' },
    h('h4', {}, `入力シートの読み取り結果：${fileName}`, h('span', { class: 'r note' }, '受診者を決めてから内容を確認します')),
    h('div', { class: 'b' },
      h('div', { class: 'row' }, h('span', { class: 'badge wait' }, '健診回がまだ無い'), h('span', { class: 'note' }, '受診者のマス：' + (['_falco_id', '_birth', '_sex', '_date'].filter(k => hdr[k]).map(k => ({ _falco_id: '個人ID ', _birth: '生 ', _sex: '', _date: '実施日 ' })[k] + hdr[k]).join(' ／ ') || '読めず')),
        h('a', { href: '/api/import/image?file=' + encodeURIComponent(fileName), target: '_blank', class: 'btn sm' }, '写真を見る'), h('span', { class: 'note' }, (r.notes || []).join('。'))),
      existing, cands, newForm, examRow,
      h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { class: 'btn p', onclick: go }, sg.patientId ? 'この受診者に健診回を作って続ける' : '登録して続ける'), h('button', { class: 'btn', onclick: () => { host.remove(); } }, 'やめる')))));
}

// ---- 写真の読み取り結果の確認画面 ----
// 読んだ値と写真の切り抜きを並べて見せる。左右一致で妥当なものは緑、食い違い・片方だけ・陽性・桁が怪しいものは赤。人が OK して初めて保存する。
async function photoConfirm(fileName, reread) {
  const x = S.exam; if (!x) return;
  let host = $('#photoConfirm');
  if (!host) { host = h('div', { id: 'photoConfirm' }); const first = $('.content .col .card'); if (first) first.before(host); else $('.content').prepend(host); }
  host.innerHTML = ''; host.append(h('div', { class: 'card' }, h('h4', {}, '写真の読み取り結果'), h('div', { class: 'b note' }, '読み取り中…（10〜20 秒）')));
  host.scrollIntoView({ block: 'start' });
  let d;
  try { d = await api('/api/import/photo?file=' + encodeURIComponent(fileName) + (reread ? '&reread=true' : '')); } catch (e) { host.innerHTML = ''; host.append(h('div', { class: 'card' }, h('div', { class: 'b err' }, e.message))); return; }
  const r = d.result; host.innerHTML = '';
  if (r.error) { host.append(h('div', { class: 'card' }, h('h4', {}, '写真の読み取り結果'), h('div', { class: 'b' }, h('div', { class: 'err' }, r.error), h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => photoConfirm(fileName, true) }, '読み直す'), h('button', { class: 'btn', onclick: () => host.remove() }, '閉じる'))))); return; }
  const same = d.suggestedExamId === x.exam.id;
  const who = h('div', { class: 'row' },
    h('span', { class: 'badge ' + (same ? 'done' : 'wait') }, same ? 'この受診者と一致' : (d.suggestedLabel ? '別の受診者の可能性：' + d.suggestedLabel : '受診者を自動で突合できず')),
    h('span', {}, `読み取り：${r.nameKana || '氏名カナ不明'}${r.sex ? ' ' + r.sex : ''}${r.patientNo ? ' ／ 番号 ' + r.patientNo : ''}${r.birthHint ? ' ／ 生 ' + r.birthHint : ''}${r.takenAt ? ' ／ 採取日 ' + r.takenAt : ''}${r.page ? ' ／ ページ ' + r.page : ''}`),
    h('span', { class: 'note' }, `いまの受診者：${x.patient.name}（${x.patient.kana || 'カナなし'}）${x.patient.sex} ／ 健診日 ${x.exam.examDate}`));
  const inputs = {};
  const row = it => {
    const st = it.status; const cls = st === 'ok' ? 'ok' : st === 'check' ? 'chk' : 'miss';
    const inp = h('input', { type: 'text', class: 'num', value: it.value || '', 'data-photo-item': it.item, inputmode: it.kind === '定性' ? 'text' : 'decimal' });
    inputs[it.item] = inp;
    inp.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === 'ArrowDown') { e.preventDefault(); const all = $$('input[data-photo-item]'); const i = all.indexOf(inp); if (i + 1 < all.length) all[i + 1].focus(); } else if (e.key === 'ArrowUp') { e.preventDefault(); const all = $$('input[data-photo-item]'); const i = all.indexOf(inp); if (i > 0) all[i - 1].focus(); } });
    const crop = d.crops[it.item] ? h('img', { src: d.crops[it.item], class: 'crop', title: '写真の切り抜き（左の写し）' }) : h('span', { class: 'note' }, '—');
    return h('tr', { class: 'ph ' + cls },
      h('td', {}, h('span', { class: 'badge ' + (st === 'ok' ? 'auto' : 'wait') }, st === 'ok' ? '一致' : st === 'check' ? '要確認' : '未読')),
      h('td', {}, it.item), h('td', { class: 'num' }, inp), h('td', {}, crop),
      h('td', { class: 'note' }, `${it.left || '—'}${it.flag ? ' ' + it.flag : ''} ／ ${it.right || '—'}`), h('td', { class: 'note' }, it.refText), h('td', { class: st === 'ok' ? 'note' : 'err' }, it.reason));
  };
  const checks = r.items.filter(i => i.status !== 'ok').length;
  const table = h('table', { class: 'photo' }, h('tr', {}, h('th', {}, '状態'), h('th', {}, '項目'), h('th', { style: 'text-align:right' }, '値（直せる）'), h('th', {}, '写真'), h('th', {}, '左 ／ 右の読み'), h('th', {}, '基準値'), h('th', {}, '')), ...r.items.map(row));
  const save = async () => {
    const items = Object.entries(inputs).map(([item, el]) => ({ item, value: el.value.trim() })).filter(i => i.value);
    if (!same && !confirm('読み取った氏名・番号が、いまの受診者と一致していません。それでも ' + x.patient.name + ' に入れますか？')) return;
    if (!confirm(`${items.length} 項目を ${x.patient.name} の血液検査に保存します。要確認の項目は紙と見比べましたか？`)) return;
    try { const res = await api('/api/import/apply-photo', { method: 'POST', body: { file: fileName, examId: x.exam.id, items, takenAt: r.takenAt || '' } }); toast(res.message + `（${res.count} 項目）`); applyDto(res.dto); host.remove(); renderMain(true); } catch (e) { toast(e.message, true); }
  };
  host.append(h('div', { class: 'card' },
    h('h4', {}, `写真の読み取り結果：${fileName}`, h('span', { class: 'r note' }, `${r.items.length} 項目（一致 ${r.okCount}、要確認 ${checks}）。要確認は紙と見比べて、値を直すか空にする`)),
    h('div', { class: 'b' }, who,
      h('div', { class: 'row' }, h('a', { href: '/api/import/image?file=' + encodeURIComponent(fileName), target: '_blank', class: 'btn sm' }, '写真を大きく見る'), h('span', { class: 'note' }, (r.notes || []).join('。'))),
      h('div', { style: 'padding:0' }, table),
      h('div', { class: 'row', style: 'margin-top:8px' },
        h('button', { class: 'btn p', onclick: save }, `この内容で保存`),
        h('button', { class: 'btn', onclick: () => photoConfirm(fileName, true) }, '読み直す'),
        h('button', { class: 'btn', onclick: () => host.remove() }, 'やめる')))));
  const firstCheck = r.items.find(i => i.status !== 'ok'); if (firstCheck && inputs[firstCheck.item]) inputs[firstCheck.item].focus();
}

// ---- 総合判定・印刷タブ ----
function tabOverall() {
  const x = S.exam; const e = x.exam;
  const order = [...S.masters.sections, ...S.masters.bloodCategories.map(c => '血液:' + c)];
  const rows = order.filter(s => x.judgments.some(j => j.section === s)).map(s => { const j = x.judgments.find(k => k.section === s); return h('tr', {}, h('td', { style: 'white-space:nowrap' }, s), h('td', { class: 'note' }, j.reason), h('td', { style: 'white-space:nowrap' }, sectionLabel(s, j.code)), h('td', {}, judgBadge(j))); });
  const hSel = selectInput('comment_h', S.masters.comments.filter(c => c.code.startsWith('H')).map(c => ({ value: c.code, label: `${c.code} ${c.text}` })));
  const cSel = selectInput('comment_c', S.masters.comments.filter(c => c.code.startsWith('C')).map(c => ({ value: c.code, label: `${c.code} ${c.text}` })));
  if (!val('comment_h')) hSel.value = x.suggest.h; if (!val('comment_c')) cSel.value = x.suggest.c;
  const overall = x.judgments.find(j => j.section === '総合');
  // 所見のまとめ（先生が見る順：所見 → コメント → 保存）
  const findingSecs = S.masters.findingSections.filter(sec => x.findings.some(f => f.section === sec) || x.judgments.some(j => j.section === sec));
  const findingRows = findingSecs.map(sec => { const fs = x.findings.filter(f => f.section === sec); const j = x.judgments.find(k => k.section === sec); return h('tr', {}, h('td', { style: 'white-space:nowrap;font-weight:bold' }, sec), h('td', {}, fs.length ? fs.map(f => (f.code ? f.code + ' ' : '') + f.text).join('、') : h('span', { class: 'note' }, '所見が未入力')), h('td', { style: 'white-space:nowrap' }, j ? sectionLabel(sec, j.code) : '')); });
  const left = h('div', { class: 'col', style: 'flex:3' },
    h('div', { class: 'card' }, h('h4', {}, '1. 所見（先生）', h('span', { class: 'r note' }, '所見タブで追加・削除。判定は自動で付き、変えたければ下の「判定の一覧」で')),
      h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '検査'), h('th', {}, '所見'), h('th', {}, '判定')), ...findingRows, findingRows.length ? null : h('tr', {}, h('td', { colspan: 3, class: 'note' }, 'このコースに所見の検査はありません'))))),
    h('div', { class: 'card' }, h('h4', {}, '2. 結果のお知らせに印字するコメント', h('span', { class: 'r note' }, '自動で入っています。必要なら変える・書き足す')),
      h('div', { class: 'b' },
        h('div', { class: 'row' }, h('label', {}, '総合判定'), h('b', { style: 'font-size:16px' }, overall ? sectionLabel('総合', overall.code) : '—'), h('span', { class: 'note' }, '（自動。各検査で最も重いもの）')),
        h('div', { class: 'row', style: 'margin-top:8px' }, h('label', {}, '判定コメント H'), hSel), h('div', { class: 'row' }, h('label', {}, '生活習慣 C'), cSel),
        h('div', { class: 'note' }, `候補：${x.suggest.cCandidates.join('、')}（既定 ${x.suggest.c}）。H は総合判定から ${x.suggest.h}。`),
        h('div', { class: 'row', style: 'margin-top:8px' }, h('label', {}, '追加コメント'), textInput('free_comment', '自由記述（任意。そのまま印字）')),
        h('div', { class: 'row' }, h('label', {}, '次回カメラ'), `胃 ${val('next_gastro_years') || '—'} 年後 ／ 大腸 ${val('next_colon_years') || '—'} 年後（所見タブで選ぶ）`))),
    h('div', { class: 'card' }, h('h4', {}, '3. 保存（確定）と印刷'), h('div', { class: 'b' },
      x.missing.length ? h('div', { class: 'err', style: 'margin-bottom:6px' }, '未入力の必須項目：' + x.missing.join('、')) : null,
      h('div', { class: 'row' },
        isLocked() ? h('button', { class: 'btn', onclick: async () => { if (!confirm('確定を解除しますか？')) return; applyDto(await api(`/api/exams/${e.id}/unconfirm`, { method: 'POST' })); renderMain(); } }, '確定を解除')
          : h('button', { class: 'btn p', onclick: async () => { if (x.missing.length && !confirm('未入力の必須項目があります：' + x.missing.join('、') + '\nそれでも確定しますか？')) return; if (!confirm('この内容で確定し、結果のお知らせを保存します。よろしいですか？')) return; try { applyDto(await api(`/api/exams/${e.id}/confirm`, { method: 'POST' })); renderMain(); toast('確定しました'); } catch (err) { toast(err.message, true); } } }, '問題なければ保存（確定）'),
        e.status === '確定' ? h('button', { class: 'btn', onclick: async () => { applyDto(await api('/api/exams/' + e.id, { method: 'PUT', body: { status: '郵送済' } })); renderMain(); toast('郵送済にしました'); } }, '郵送済にする') : null),
      h('div', { class: 'row', style: 'margin-top:8px' }, h('button', { class: 'btn p', onclick: () => openPdf('all') }, '3 種まとめて印刷（受診票＋結果のお知らせ＋説明）'), h('button', { class: 'btn', onclick: () => openPdf('notice') }, '結果のお知らせだけ'), h('button', { class: 'btn', onclick: () => openPdf('explanation') }, '検査内容の説明だけ')),
      h('div', { class: 'note' }, '印刷は PC に USB でつないだプリンターで。確定すると判定が固定され、結果のお知らせの PDF が data\\pdf に保存されます。'))),
    x.issued.length ? h('div', { class: 'card' }, h('h4', {}, '発行した PDF'), h('div', { class: 'b' }, h('table', {}, ...x.issued.map(p => h('tr', {}, h('td', {}, p.createdAt), h('td', {}, p.kind), h('td', {}, h('a', { href: `/api/exams/${e.id}/issued/${p.id}`, target: '_blank' }, p.fileName))))))) : null);
  const right = h('div', { class: 'col', style: 'flex:1.6' },
    h('details', { class: 'card' }, h('summary', { style: 'padding:8px 10px;cursor:pointer;font-weight:bold' }, '判定の一覧（自動。確認したいときだけ開く）'),
      h('div', { class: 'b' }, h('div', { class: 'row' }, judgeBox('総合')), h('div', { class: 'note' }, '先生が変えると「手動」になり、結果のお知らせにはその判定が印字されます。'),
        h('table', {}, h('tr', {}, h('th', {}, '項目'), h('th', {}, '今回の要点'), h('th', {}, '判定'), h('th', {}, '')), ...rows, h('tr', {}, h('td', {}, 'メタボ'), h('td', { class: 'note' }, x.metabolic.detail), h('td', {}, x.metabolic.label), h('td', {}, h('span', { class: 'badge auto' }, '自動')))))),
    h('div', { class: 'card' }, h('h4', {}, '比較'), h('div', { class: 'b note' }, `前回 ${x.history[0]?.examDate || '—'} ／ 前々回 ${x.history[1]?.examDate || '—'}（履歴から自動）`)));
  return h('div', { class: 'cols' }, left, right);
}
function openPdf(kind) { window.open(`/api/exams/${S.exam.exam.id}/pdf/${kind}`, '_blank'); }

// ---------- ほかの画面 ----------
function viewOther() {
  const box = h('div', {});
  (async () => {
    try {
      if (S.view === 'pending') {
        const list = await api('/api/exams/pending');
        box.append(h('div', { class: 'card' }, h('h4', {}, '未完了一覧（過去 90 日、郵送済を除く）'), h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '実施日'), h('th', {}, '氏名'), h('th', {}, 'コース'), h('th', {}, '状態'), h('th', {}, '更新')), ...list.map(e => h('tr', { class: 'click', onclick: () => { S.view = 'day'; S.date = e.examDate; $('#date').value = S.date; setViewButtons(); openExam(e.id); } }, h('td', {}, e.examDate), h('td', {}, e.name), h('td', {}, e.course), h('td', {}, statusBadge(e.status)), h('td', { class: 'note' }, e.updatedAt)))))));
      } else if (S.view === 'import') {
        const imp = await api('/api/import/files');
        box.append(h('div', { class: 'card' }, h('h4', {}, '採血の取り込み', h('span', { class: 'r' }, h('button', { class: 'btn', onclick: () => renderMain() }, '読み直す'))), h('div', { class: 'b' },
          h('div', { class: 'row' }, h('label', {}, '取込フォルダ'), imp.dir), h('div', { class: 'note' }, 'HR-f で検査結果画面を「Microsoft Print to PDF」で印刷し、USB で運んでこのフォルダに置きます。受診者を開いて「血液検査」タブから取り込みます。'),
          h('table', {}, h('tr', {}, h('th', {}, 'ファイル'), h('th', {}, '採取日'), h('th', {}, '読み取った氏名・ID'), h('th', {}, '突合'), h('th', {}, '項目'), h('th', {}, '')),
            ...imp.files.map(f => h('tr', {}, h('td', {}, f.fileName), h('td', {}, f.takenAt), h('td', {}, (f.nameHint || '') + (f.idHint ? ' ／ ' + f.idHint : '')), h('td', {}, f.imported ? h('span', { class: 'badge done' }, f.imported) : h('span', { class: 'badge ' + (f.match === '一致' ? 'done' : 'wait') }, f.match + (f.suggestedLabel ? '：' + f.suggestedLabel : ''))), h('td', {}, f.error ? h('span', { class: 'err' }, f.error) : f.itemCount + ' 項目'),
              h('td', {}, f.suggestedExamId && !f.imported ? h('button', { class: 'btn sm', onclick: async () => { S.view = 'day'; setViewButtons(); await openExam(f.suggestedExamId); S.tab = 'blood'; renderMain(); } }, '受診者を開く') : null)))))));
      } else if (S.view === 'followup') {
        const list = await api('/api/exams/followup');
        box.append(h('div', { class: 'card' }, h('h4', {}, '要受診・要再検査・要精密検査・要治療の人（過去 1 年）'), h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '実施日'), h('th', {}, '氏名'), h('th', {}, '該当項目'), h('th', {}, '状態')), ...list.map(r => h('tr', { class: 'click', onclick: () => { S.view = 'day'; S.date = r.exam.examDate; $('#date').value = S.date; setViewButtons(); openExam(r.exam.id); } }, h('td', {}, r.exam.examDate), h('td', {}, r.exam.name), h('td', {}, r.items.map(i => `${i.section}：${i.name}`).join('、')), h('td', {}, statusBadge(r.exam.status))))), h('div', { class: 'b note' }, '来院したかの記録は第 4 段階で付けます。'))));
      } else if (S.view === 'events') {
        const list = await api('/api/events');
        box.append(h('div', { class: 'card' }, h('h4', {}, '記録（いつ・何を）'), h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '日時'), h('th', {}, '何を'), h('th', {}, '健診回'), h('th', {}, '詳細')), ...list.map(ev => h('tr', {}, h('td', {}, ev.ts), h('td', {}, ev.kind), h('td', {}, ev.examId ?? ''), h('td', { class: 'note' }, ev.detail)))))));
      }
    } catch (e) { box.append(h('div', { class: 'err' }, e.message)); }
  })();
  return box;
}
function setViewButtons() { $$('.menubar button[data-view]').forEach(b => b.classList.toggle('on', b.dataset.view === S.view)); }


// ---------- 起動 ----------
async function init() {
  $('#date').value = S.date;
  $('#date').addEventListener('change', async ev => { S.date = ev.target.value; S.exam = null; await loadDay(); renderMain(); });
  $('#today').addEventListener('click', async () => { S.date = today(); $('#date').value = S.date; S.exam = null; await loadDay(); renderMain(); });
  $('#addPatient').addEventListener('click', () => { if (!S.masters) return; S.view = 'day'; setViewButtons(); S.exam = null; S.addingPatient = true; $('#side').classList.remove('open'); renderMain(); S.addingPatient = false; });
  $('#hamb').addEventListener('click', () => $('#side').classList.toggle('open'));
  $$('.menubar button[data-view]').forEach(b => b.addEventListener('click', () => { S.view = b.dataset.view; setViewButtons(); renderMain(); }));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) flushSaves(); });
  await pollStatus(); setInterval(pollStatus, 10000);
  try { S.masters = await api('/api/masters'); } catch (e) { return; }
  document.body.dataset.ready = '1';
  await loadDay();
  // 起動したら当日の最初の未完了の受診者を開く（入力中を優先）
  const first = S.exams.find(e => e.status === '入力中') || S.exams.find(e => e.status !== '確定' && e.status !== '郵送済');
  if (first) { S.tab = 'measure'; await openExam(first.id); } else renderMain();
  // QR（受診票）をバーコードリーダーで読んだとき：kenshin:exam:123 が打ち込まれる
  let buf = ''; document.addEventListener('keydown', e => { if (e.target && e.target.tagName === 'INPUT' && e.target.type !== 'date') return; if (e.key === 'Enter') { const m = buf.match(/kenshin:exam:(\d+)/); buf = ''; if (m) { S.view = 'day'; setViewButtons(); openExam(+m[1]); } } else if (e.key.length === 1) { buf += e.key; if (buf.length > 40) buf = buf.slice(-40); } });
}
init();
})();
