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
    $('#status').textContent = (st.clinic?.name || '') + '　音声: ' + (st.voice?.available ? '有効' : '無効') + '　v' + (st.version || '');
  } catch (e) { }
}

// ---------- 名前・ラベル ----------
const catName = code => (S.masters.categories.find(c => c.code === code) || {}).name || code || '';
const sectionLabel = (section, code) => (S.masters.sectionLabels[section] || {})[code] || catName(code);
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
  await loadDay(); await openExam(e.id); S.tab = 'measure'; renderMain();
}

// ---------- 健診回を開く ----------
async function openExam(id) {
  S.exam = await api('/api/exams/' + id);
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
function focusNext(el) { const all = $$('input[data-key]:not([disabled]), select[data-key]:not([disabled])'); const i = all.indexOf(el); if (i >= 0 && i + 1 < all.length) all[i + 1].focus(); }
function radio(key, options, labels) {
  const g = h('span', { class: 'radio', 'data-key': key, 'data-radio': '1' });
  options.forEach((o, i) => {
    const inp = h('input', { type: 'radio', name: key, value: o, disabled: isLocked() ? '' : null });
    if (val(key) === o) inp.checked = true;
    inp.addEventListener('change', () => queueSave(key, o));
    g.append(h('label', { onfocus: () => { S.focusKey = key; } }, inp, h('span', {}, labels ? labels[i] : o)));
  });
  g.addEventListener('click', () => { S.focusKey = key; });
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
  const sel = h('select', { class: 'judgesel', 'data-section': section, disabled: isLocked() ? '' : null }, h('option', { value: '' }, '（未判定）'), ...S.masters.categories.map(c => h('option', { value: c.code }, `${sectionLabel(section, c.code)}（${c.code}）`)));
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
  if (!S.exam) { main.append(h('div', { class: 'content' }, S.addingPatient ? patientDialog() : h('div', { class: 'note', style: 'padding:20px' }, '左の一覧から受診者を選ぶか、「＋ 受診者」で登録してください。'))); return; }
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
  main.append(h('div', { class: 'footer' },
    h('button', { class: 'btn', onclick: () => openPdf('jushinhyo') }, '受診票を印刷'),
    h('button', { class: 'btn', onclick: () => { S.tab = 'overall'; renderMain(); } }, '総合判定へ'),
    S.status?.voice?.available ? micButton() : null,
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
function tabMeasure() {
  const secs = (S.exam.course?.sections) || [];
  const has = s => secs.includes(s) || secs.length === 0;
  const left = h('div', { class: 'col', style: 'flex:3' });
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
    h('div', { class: 'card' }, h('h4', {}, '所見から判定を決める対応表（抜粋）'), h('div', { class: 'b' }, h('table', {}, h('tr', {}, h('th', {}, '所見'), h('th', {}, '判定')), ...S.masters.findings.filter(f => f.judgment !== '150').slice(0, 12).map(f => h('tr', {}, h('td', {}, `${f.section}：${f.text}`), h('td', {}, catName(f.judgment))))), h('div', { class: 'note' }, '複数の所見があれば最も重い判定を案にします。config/所見マスター.csv で変更できます。'))));
  return h('div', { class: 'cols' }, left, picker);
}
function showPicker(picker, section) {
  const list = h('div', { class: 'list' });
  const q = h('input', { type: 'text', placeholder: 'コードか名称で検索', style: 'width:100%' });
  const free = h('input', { type: 'text', placeholder: '自由記述', style: 'width:100%' });
  const render = () => { list.innerHTML = ''; const t = q.value.trim(); for (const f of S.masters.findings.filter(f => f.section === section && (!t || f.code.includes(t) || f.text.includes(t)))) list.append(h('div', { onclick: () => add(f.code, null) }, `${f.code}　${f.text}${f.note === '仮' ? '（仮）' : ''}　→ ${catName(f.judgment)}`)); };
  const add = async (code, text) => { try { applyDto(await api(`/api/exams/${S.exam.exam.id}/findings`, { method: 'POST', body: { section, code, text } })); renderMain(true); showPicker($$('.col')[1], section); } catch (e) { toast(e.message, true); } };
  q.addEventListener('input', render); render();
  picker.innerHTML = '';
  picker.append(h('div', { class: 'card' }, h('h4', {}, '所見を追加 ― ' + section), h('div', { class: 'b' }, h('div', { class: 'row' }, q), list, h('div', { class: 'row', style: 'margin-top:8px' }, free, h('button', { class: 'btn', onclick: () => { if (free.value.trim()) add(null, free.value.trim()); } }, '自由記述で追加')))));
  q.focus();
}

// ---- 血液タブ ----
function tabBlood() {
  const x = S.exam;
  const left = h('div', { class: 'col', style: 'flex:2' });
  const importCard = h('div', { class: 'card' }, h('h4', {}, '取り込み元', h('span', { class: 'r' }, h('button', { class: 'btn', onclick: () => loadImport(importCard, true) }, '取込フォルダを読み直す'))), h('div', { class: 'b' }, h('div', { class: 'note' }, '読み込み中…')));
  left.append(importCard); loadImport(importCard, false);
  const rows = x.blood.map(b => h('tr', {}, h('td', {}, b.category), h('td', {}, b.item), h('td', { class: 'num' }, b.value), h('td', {}, b.unit), h('td', {}, b.refText), h('td', { class: b.flag === '↑' ? 'hi' : b.flag === '↓' ? 'lo' : '' }, b.flag), h('td', {}, isLocked() ? null : h('button', { class: 'btn sm', onclick: async () => { applyDto(await api(`/api/exams/${x.exam.id}/blood/${b.id}`, { method: 'DELETE' })); renderMain(true); } }, '消す'))));
  const add = { item: h('input', { type: 'text', placeholder: '項目名', style: 'width:150px' }), value: h('input', { type: 'text', class: 'num', placeholder: '結果' }), unit: h('input', { type: 'text', class: 'sm', placeholder: '単位' }), ref: h('input', { type: 'text', placeholder: '基準値（例 39~98、5.0以下）', style: 'width:150px' }) };
  left.append(h('div', { class: 'card' }, h('h4', {}, `血液検査の値（${x.blood.length} 項目）`, h('span', { class: 'r note' }, '↑↓ は基準値外。取り込みがうまくいかないときは下で手入力')),
    h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '分野'), h('th', {}, '項目'), h('th', { style: 'text-align:right' }, '結果'), h('th', {}, '単位'), h('th', {}, '基準値'), h('th', {}, ''), h('th', {}, '')), ...rows)),
    isLocked() ? null : h('div', { class: 'b row' }, add.item, add.value, add.unit, add.ref, h('button', { class: 'btn sm p', onclick: async () => { try { applyDto(await api(`/api/exams/${x.exam.id}/blood`, { method: 'PUT', body: { item: add.item.value, value: add.value.value, unit: add.unit.value, refText: add.ref.value } })); renderMain(true); } catch (e) { toast(e.message, true); } } }, '追加・更新'))));
  const right = h('div', { class: 'col', style: 'flex:1.3;min-width:300px' },
    h('div', { class: 'card' }, h('h4', {}, '血液検査判定（分野ごと）'), h('div', { class: 'b' }, ...S.masters.bloodCategories.map(cat => { const sec = '血液:' + cat; const j = x.judgments.find(k => k.section === sec); const has = x.blood.some(b => b.category === cat); return h('div', { style: 'border-bottom:1px solid #eee;padding:6px 0' }, h('div', { class: 'row', style: 'margin:0' }, h('label', { style: 'min-width:70px;font-weight:bold' }, cat), has ? judgeBox(sec) : h('span', { class: 'note' }, '（項目なし）')), j && j.reason ? h('div', { class: 'note', 'data-reason': sec }, j.reason) : null); }), h('div', { class: 'note', style: 'margin-top:6px' }, '規則：分野内に基準値外が 1 つでもあれば要経過観察（仮）。大きく外れたときの閾値は config/判定基準.json の severe。'))),
    h('div', { class: 'card' }, h('h4', {}, 'メタボリックシンドローム判定'), h('div', { class: 'b' }, h('div', { class: 'row' }, h('span', { style: 'font-size:18px;font-weight:bold' }, x.metabolic.label), h('span', { class: 'badge auto' }, '自動')), h('div', { class: 'note' }, x.metabolic.detail), h('div', { class: 'note', style: 'margin-top:6px' }, x.metabolic.comment))));
  return h('div', { class: 'cols' }, left, right);
}
async function loadImport(cardEl, rescan) {
  try {
    const imp = await api('/api/import/files');
    const body = cardEl.querySelector('.b'); body.innerHTML = '';
    body.append(h('div', { class: 'row' }, h('label', {}, '取込フォルダ'), h('span', {}, imp.dir), h('span', { class: 'note' }, 'HR-f で「Microsoft Print to PDF」に印刷した PDF を置く')));
    if (imp.files.length === 0) { body.append(h('div', { class: 'note' }, 'PDF がありません')); return; }
    body.append(h('table', {}, h('tr', {}, h('th', {}, 'ファイル'), h('th', {}, '採取日'), h('th', {}, '読み取った氏名・ID'), h('th', {}, '突合'), h('th', {}, '項目'), h('th', {}, '')),
      ...imp.files.map(f => h('tr', {}, h('td', {}, f.fileName), h('td', {}, f.takenAt), h('td', {}, (f.nameHint || '') + (f.idHint ? ' ／ ' + f.idHint : '')),
        h('td', {}, f.imported ? h('span', { class: 'badge done' }, f.imported) : h('span', { class: 'badge ' + (f.match === '一致' ? 'done' : 'wait') }, f.match + (f.suggestedLabel ? '：' + f.suggestedLabel : ''))),
        h('td', {}, f.error ? h('span', { class: 'err' }, f.error) : f.itemCount + ' 項目'),
        h('td', {}, isLocked() || f.error ? null : h('button', { class: 'btn sm p', onclick: async () => { if (!confirm(`「${f.fileName}」を ${S.exam.patient.name} の血液検査として取り込みます。よろしいですか？`)) return; try { const r = await api('/api/import/apply', { method: 'POST', body: { file: f.fileName, examId: S.exam.exam.id } }); toast(r.message + `（${r.count} 項目）`); applyDto(r.dto); renderMain(true); } catch (e) { toast(e.message, true); } } }, 'この受診者に取り込む'))))));
  } catch (e) { cardEl.querySelector('.b').textContent = e.message; }
}

// ---- 総合判定・印刷タブ ----
function tabOverall() {
  const x = S.exam; const e = x.exam;
  const order = [...S.masters.sections, ...S.masters.bloodCategories.map(c => '血液:' + c)];
  const rows = order.filter(s => x.judgments.some(j => j.section === s)).map(s => { const j = x.judgments.find(k => k.section === s); return h('tr', {}, h('td', { style: 'white-space:nowrap' }, s), h('td', { class: 'note' }, j.reason), h('td', { style: 'white-space:nowrap' }, sectionLabel(s, j.code)), h('td', {}, judgBadge(j))); });
  const hSel = selectInput('comment_h', S.masters.comments.filter(c => c.code.startsWith('H')).map(c => ({ value: c.code, label: `${c.code} ${c.text}` })));
  const cSel = selectInput('comment_c', S.masters.comments.filter(c => c.code.startsWith('C')).map(c => ({ value: c.code, label: `${c.code} ${c.text}` })));
  if (!val('comment_h')) hSel.value = x.suggest.h; if (!val('comment_c')) cSel.value = x.suggest.c;
  const left = h('div', { class: 'col', style: 'flex:3' },
    h('div', { class: 'card' }, h('h4', {}, '判定の一覧（受診票と同じ並び）'), h('div', { class: 'b', style: 'padding:0' }, h('table', {}, h('tr', {}, h('th', {}, '項目'), h('th', {}, '今回の要点'), h('th', {}, '判定'), h('th', {}, '')), ...rows, h('tr', {}, h('td', {}, 'メタボ'), h('td', { class: 'note' }, x.metabolic.detail), h('td', {}, x.metabolic.label), h('td', {}, h('span', { class: 'badge auto' }, '自動')))))),
    x.missing.length ? h('div', { class: 'card' }, h('h4', {}, '未入力の必須項目'), h('div', { class: 'b err' }, x.missing.join('、') + '（コース「' + e.course + '」の必須項目）')) : null);
  const right = h('div', { class: 'col', style: 'flex:1.6' },
    h('div', { class: 'card' }, h('h4', {}, '総合判定'), h('div', { class: 'b' },
      h('div', { class: 'row' }, judgeBox('総合')), h('div', { class: 'note' }, '各項目で最も重い判定を案にしています。先生が変えると「手動」になります。'),
      h('div', { class: 'row', style: 'margin-top:8px' }, h('label', {}, '判定コメント H'), hSel), h('div', { class: 'row' }, h('label', {}, '生活習慣 C'), cSel),
      h('div', { class: 'note' }, `候補：${x.suggest.cCandidates.join('、')}（既定 ${x.suggest.c}）。H は区分 ${sectionLabel('総合', (x.judgments.find(j => j.section === '総合') || {}).code)} から ${x.suggest.h}。確定時に未選択なら候補が入ります。`),
      h('div', { class: 'row', style: 'margin-top:8px' }, h('label', {}, '追加コメント'), textInput('free_comment', '自由記述（任意）')))),
    h('div', { class: 'card' }, h('h4', {}, '印刷'), h('div', { class: 'b' },
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: () => openPdf('jushinhyo') }, '受診票（A4）'), h('button', { class: 'btn p', onclick: () => openPdf('notice') }, '結果のお知らせ（A4×4、小冊子印刷で A3 二つ折り）'), h('button', { class: 'btn', onclick: () => openPdf('explanation') }, '検査内容の説明（A4 両面）')),
      h('div', { class: 'row' }, h('label', {}, '次回カメラ'), `胃 ${val('next_gastro_years') || '—'} 年後 ／ 大腸 ${val('next_colon_years') || '—'} 年後（所見タブで選ぶ）`),
      h('div', { class: 'row' }, h('label', {}, '比較'), `前回 ${x.history[0]?.examDate || '—'} ／ 前々回 ${x.history[1]?.examDate || '—'}（履歴から自動）`),
      h('div', { class: 'row', style: 'margin-top:8px' },
        isLocked() ? h('button', { class: 'btn', onclick: async () => { if (!confirm('確定を解除しますか？')) return; applyDto(await api(`/api/exams/${e.id}/unconfirm`, { method: 'POST' })); renderMain(); } }, '確定を解除')
          : h('button', { class: 'btn p', onclick: async () => { if (x.missing.length && !confirm('未入力の必須項目があります：' + x.missing.join('、') + '\nそれでも確定しますか？')) return; try { const r = await api(`/api/exams/${e.id}/confirm`, { method: 'POST' }); applyDto(r.dto); toast('確定しました' + (r.pdf ? '。結果のお知らせの PDF を保存しました' : '')); renderMain(); } catch (err) { toast(err.message, true); } } }, '確定する'),
        e.status === '確定' ? h('button', { class: 'btn', onclick: async () => { applyDto(await api('/api/exams/' + e.id, { method: 'PUT', body: { status: '郵送済' } })); renderMain(); toast('郵送済にしました'); } }, '郵送済にする') : null),
      h('div', { class: 'note' }, '確定すると判定が固定され、結果のお知らせの PDF が data\\pdf に保存されます。やり直すには「確定を解除」。'))),
    x.issued.length ? h('div', { class: 'card' }, h('h4', {}, '発行した PDF'), h('div', { class: 'b' }, h('table', {}, ...x.issued.map(p => h('tr', {}, h('td', {}, p.createdAt), h('td', {}, p.kind), h('td', {}, h('a', { href: `/api/exams/${e.id}/issued/${p.id}`, target: '_blank' }, '開く'))))))) : null);
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

// ---------- 音声入力 ----------
function micButton() {
  const b = h('button', { class: 'mic', title: '押している間、話す（数字・なし／あり・プラス／マイナス・つぎ）' }, '🎤');
  const start = async ev => { ev.preventDefault(); if (Voice.recording) return; try { await Voice.start(applyVoice); b.classList.add('on'); } catch (e) { toast(e.message, true); } };
  const stop = ev => { ev.preventDefault(); if (!Voice.recording) return; b.classList.remove('on'); Voice.stop(); };
  b.addEventListener('pointerdown', start); b.addEventListener('pointerup', stop); b.addEventListener('pointerleave', stop); b.addEventListener('pointercancel', stop);
  return b;
}
function applyVoice(r) {
  if (r.error) { toast(r.error, true); return; }
  const p = r.parsed || {}; const nums = p.numbers || []; const cmds = p.commands || [];
  toast('聞き取り：' + (r.text || '（無音）') + (nums.length ? '　→ ' + nums.join(' ') : ''));
  const inputs = $$('input[data-key]:not([disabled])');
  let target = document.activeElement && document.activeElement.dataset && document.activeElement.dataset.key ? document.activeElement : inputs.find(i => i.dataset.key === S.focusKey) || inputs.find(i => !i.value) || inputs[0];
  // 選択式（なし／あり、−／±／+）
  const group = S.focusKey && $(`[data-radio][data-key="${S.focusKey}"]`);
  if (group && cmds.length && nums.length === 0) {
    for (const c of cmds) { const inp = $$('input[type=radio]', group).find(i => i.value === c); if (inp) { inp.checked = true; queueSave(S.focusKey, c); const all = $$('[data-radio]'); const i = all.indexOf(group); if (all[i + 1]) { S.focusKey = all[i + 1].dataset.key; all[i + 1].scrollIntoView({ block: 'center' }); } return; } }
  }
  if (cmds.includes('clear') && target) { target.value = ''; queueSave(target.dataset.key, ''); return; }
  if (!target) return;
  // 聞き取りの繰り返し（63636363）や桁違いは欄に入れない
  const sane = nums.filter(n => Number.isFinite(n) && String(Math.trunc(Math.abs(n))).length <= 4);
  if (sane.length < nums.length) toast('桁が多すぎる数値は無視しました', true);
  if (sane.length) {
    let el = target;
    for (const n of sane) {
      if (!el) break;
      el.value = String(n); el.dispatchEvent(new Event('input'));
      const i = inputs.indexOf(el); el = inputs[i + 1] || null;
    }
    if (el) { el.focus(); S.focusKey = el.dataset.key; }
  }
  if (cmds.includes('next')) { const i = inputs.indexOf(target); if (inputs[i + 1]) inputs[i + 1].focus(); }
  if (cmds.includes('back')) { const i = inputs.indexOf(target); if (inputs[i - 1]) inputs[i - 1].focus(); }
}

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
  await loadDay(); renderMain();
  // QR（受診票）をバーコードリーダーで読んだとき：kenshin:exam:123 が打ち込まれる
  let buf = ''; document.addEventListener('keydown', e => { if (e.target && e.target.tagName === 'INPUT' && e.target.type !== 'date') return; if (e.key === 'Enter') { const m = buf.match(/kenshin:exam:(\d+)/); buf = ''; if (m) { S.view = 'day'; setViewButtons(); openExam(+m[1]); } } else if (e.key.length === 1) { buf += e.key; if (buf.length > 40) buf = buf.slice(-40); } });
}
init();
})();
