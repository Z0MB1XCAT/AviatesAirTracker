/* ═══════════════════════════════════════════════════════════
   AviatesAir EFB — client (iOS springboard)
   Vanilla JS, no build step. Talks to the desktop host over a
   token-guarded REST API + WebSocket.
   ═══════════════════════════════════════════════════════════ */
(() => {
'use strict';
const $  = (s,r=document)=>r.querySelector(s);
const $$ = (s,r=document)=>[...r.querySelectorAll(s)];
const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
const num=(v,d=0)=>(v==null||isNaN(v))?'—':Number(v).toFixed(d);
const esc=t=>{const d=document.createElement('div');d.textContent=t==null?'':t;return d.innerHTML;};

// ── token / auth ──────────────────────────────────────────
let token='';
(function(){ const m=location.hash.match(/t=([a-f0-9]+)/i);
  if(m){token=m[1];localStorage.setItem('efbToken',token);history.replaceState(null,'',location.pathname);}
  else token=localStorage.getItem('efbToken')||''; })();
async function api(p,o={}){ o.headers=Object.assign({'X-EFB-Token':token,'Content-Type':'application/json'},o.headers||{});
  const r=await fetch(p,o); if(r.status===401){showPairing();throw new Error('unauthorized');} return r; }
const getJSON =async p=>(await api(p)).json();
const postJSON=async(p,b)=>(await api(p,{method:'POST',body:JSON.stringify(b||{})})).json();

// ── state ─────────────────────────────────────────────────
let state={}, plan={hasPlan:false}, checklists=[], clBundle={sets:[],checklists:[],style:'generic'}, msg={friends:[],messages:[],unreadCount:0}, aircraft=[];
let activeApp=null, appCleanup=null;

// ── units: ONE weight unit (KG | LB) for every app, kept on the host so every paired device
// agrees. Fuel and gross weights show in it; perf weights use its big unit (tonnes or 1000 LB).
const LB_KG=2.20462;
let prefs={weightUnit:'KG'};
try{ const w=localStorage.getItem('efbWeightUnit'); if(w==='LB'||w==='KG') prefs.weightUnit=w; }catch{}
const WU=()=>prefs.weightUnit==='LB'?'LB':'KG';
const wLb=lb=>WU()==='LB'?lb:lb/LB_KG;                 // pounds → display unit
const fmtW=(lb,u=true)=>lb==null||isNaN(lb)?'—':Math.round(wLb(lb)).toLocaleString('en-US')+(u?' '+WU():'');
const BWU=()=>WU()==='LB'?'1000 LB':'t';               // perf big unit
const bigFromKg=kg=>WU()==='LB'?kg*LB_KG/1000:kg/1000;
const bigToLb=v=>WU()==='LB'?v*1000:v*1000*LB_KG;
function applyPrefs(p){
  if(!p||(p.weightUnit!=='KG'&&p.weightUnit!=='LB')) return;
  const old=WU(); prefs={...prefs,...p};
  try{ localStorage.setItem('efbWeightUnit',prefs.weightUnit); }catch{}
  if(old===WU()) return;
  // perf inputs typed in the old big unit follow the switch
  const conv=v=>{ const x=parseFloat(v); if(!(x>0)) return v; const lb=old==='LB'?x*1000:x*1000*LB_KG; return (WU()==='LB'?lb/1000:lb/LB_KG/1000).toFixed(1); };
  if(typeof dep!=='undefined'&&dep) dep.tow=conv(dep.tow);
  if(typeof arr!=='undefined'&&arr) arr.lw=conv(arr.lw);
  if(activeApp&&!['game','scratch','messages'].includes(activeApp)) renderApp();
  renderLive();
}

// The server sends the active per-aircraft set as a bundle; `checklists` is its list.
function setChecklists(b){ if(!b||!Array.isArray(b.checklists))return; clBundle=b; checklists=b.checklists; }
async function boot(){
  try{ const h=await getJSON('/efb/api/hello'); if(!h.tokenValid){showPairing();return;} hello=h||{}; }
  catch{ showPairing(); return; }
  $('#pairing').classList.add('hidden'); $('#app').classList.remove('hidden');
  uiApply(); loadPrefs(); buildSpringboard(); initHomeIndicator(); loadWallpaper();
  try{ aircraft=await getJSON('/efb/api/aircraft'); }catch{}
  try{ state=await getJSON('/efb/api/state'); }catch{}
  try{ plan =await getJSON('/efb/api/flightplan'); }catch{}
  try{ setChecklists(await getJSON('/efb/api/checklists')); }catch{}
  try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)) msg=m; }catch{}
  try{ applyPrefs(await getJSON('/efb/api/prefs')); }catch{}
  try{ const a=await getJSON('/efb/api/acars'); if(a&&Array.isArray(a.messages)) acars.d=a; }catch{}
  try{ man.idx=await getJSON('/efb/api/manuals'); }catch{}
  renderLive(); renderBadges(); fmInit();
  // an app opened while boot was still loading picks up the data (not the game or the scratchpad:
  // they need none of it, and a rebuild would restart a flight or drop a half-typed stamp)
  if(activeApp&&!['game','scratch'].includes(activeApp)) renderApp();
  connectWS(); startClock(); startBattery(); initFullscreen();
  const fw=$('#flightWidget');
  fw.onclick=()=>openApp('flight');
  fw.onkeydown=e=>{ if(e.key==='Enter'||e.key===' '){e.preventDefault();openApp('flight');} };
  $('#mbFlight').onclick=()=>openApp('flight');
  $('#wallpaperBtn').onclick=()=>openApp('settings');   // Wallpaper is Settings' first group
}

// ── pairing ───────────────────────────────────────────────
function showPairing(){
  $('#app').classList.add('hidden'); const p=$('#pairing'); p.classList.remove('hidden');
  const b=$$('#codeInput input'); b[0].focus();
  b.forEach((x,i)=>{ x.oninput=()=>{x.value=x.value.replace(/\D/g,'');if(x.value&&i<5)b[i+1].focus();};
    x.onkeydown=e=>{if(e.key==='Backspace'&&!x.value&&i>0)b[i-1].focus();}; });
  $('#pairBtn').onclick=doPair;
}
async function doPair(){
  const code=$$('#codeInput input').map(x=>x.value).join('');
  if(code.length!==6){$('#pairErr').textContent='Enter all 6 digits';return;}
  try{ const r=await fetch('/efb/api/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
    if(!r.ok){$('#pairErr').textContent='Incorrect code, check the desktop app';return;}
    const d=await r.json(); token=d.token; localStorage.setItem('efbToken',token); boot();
  }catch{ $('#pairErr').textContent='Connection failed'; }
}

// ── websocket ─────────────────────────────────────────────
let ws,wsRetry=0,pollTimer=null,mySocketId=null;
function connectWS(){
  const proto=location.protocol==='https:'?'wss':'ws';
  try{ ws=new WebSocket(`${proto}://${location.host}/efb/ws?t=${token}`);}catch{startPolling();return;}
  ws.onopen=()=>{wsRetry=0;stopPolling();setConn(true);};
  ws.onmessage=ev=>{ let m; try{m=JSON.parse(ev.data);}catch{return;}
    if(m.type==='snapshot'){state=m.data;renderLive();fmTick();}
    else if(m.type==='flightplan'){plan=m.data;pb.data=null;renderLive();if(['ofp','depperf','arrperf','flight'].includes(activeApp))renderApp();
      acFetch();}   // a new plan can bring the datalink its callsign
    else if(m.type==='checklists'){setChecklists(m.data);if(activeApp==='checklist')renderApp();}
    else if(m.type==='messages'){const old=msg;msg=m.data;ntMessages(old);renderBadges();if(activeApp==='messages')msRefresh();}
    else if(m.type==='welcome'){mySocketId=m.data&&m.data.id;}
    else if(m.type==='prefs'){applyPrefs(m.data);}
    else if(m.type==='acars'){const old=acars.d;acars.d=m.data;ntAcars(old);renderBadges();if(activeApp==='acars')acRefresh();}
  };
  ws.onclose=()=>{setConn(false);startPolling();wsRetry=Math.min(wsRetry+1,6);setTimeout(connectWS,800*wsRetry);};
  ws.onerror=()=>{try{ws.close();}catch{}};
}
let pollOk=false;
function startPolling(){ if(pollTimer)return; pollTimer=setInterval(async()=>{
  try{state=await getJSON('/efb/api/state');pollOk=true;renderLive();fmTick();}catch{pollOk=false;setConn(false);}},1500); }
function stopPolling(){ if(pollTimer){clearInterval(pollTimer);pollTimer=null;} }
// Status-bar signal bars = link to the desktop host: full on the live WebSocket,
// one bar while limping along on REST polling, none when the host is unreachable.
function setConn(live){
  $('#mbSignal').dataset.bars=live?'4':(pollOk?'1':'0');
}

const MONTHS=['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
function startClock(){
  const t=()=>{
    const d=new Date();
    $('#mbDate').textContent=`${String(d.getDate()).padStart(2,'0')} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
    const local=String(d.getHours()).padStart(2,'0')+':'+String(d.getMinutes()).padStart(2,'0');
    const zulu=String(d.getUTCHours()).padStart(2,'0')+':'+String(d.getUTCMinutes()).padStart(2,'0');
    $('#mbTime').textContent=`${local}L / ${zulu}Z`;
  };
  t(); setInterval(t,10000);
}
// Real device battery where the browser exposes it (Chromium/Android); iPad Safari
// doesn't, so the indicator simply stays hidden there rather than showing a made-up level.
function startBattery(){
  if(!navigator.getBattery)return;
  navigator.getBattery().then(b=>{
    const u=()=>{ const pct=Math.round(b.level*100);
      $('#mbBattWrap').classList.remove('hidden');
      $('#mbPct').textContent=pct+'%';
      const f=$('#mbBattFill'); f.style.width=pct+'%'; f.classList.toggle('low',pct<=20&&!b.charging); };
    u(); b.addEventListener('levelchange',u); b.addEventListener('chargingchange',u);
  }).catch(()=>{});
}
function initFullscreen(){
  const btn=$('#fullscreenBtn'), el=document.documentElement;
  const req=el.requestFullscreen||el.webkitRequestFullscreen;
  if(!req){ btn.classList.add('hidden'); return; }
  const isFs=()=>!!(document.fullscreenElement||document.webkitFullscreenElement);
  const sync=()=>{ btn.textContent=isFs()?'Exit full screen':'Enter full screen'; };
  btn.onclick=()=>{ if(isFs())(document.exitFullscreen||document.webkitExitFullscreen).call(document);
    else req.call(el).catch?.(()=>{}); };
  document.addEventListener('fullscreenchange',sync); document.addEventListener('webkitfullscreenchange',sync);
  sync();
}

// ── wallpaper ─────────────────────────────────────────────
let wpUploaded=false;
async function loadWallpaper(){
  wpUploaded=false;
  // 1. pilot-uploaded wallpaper (stored on the desktop host, shared across devices)
  try{
    const r=await fetch('/efb/api/wallpaper',{headers:{'X-EFB-Token':token}});
    if(r.status===200){ const blob=await r.blob(); const url=URL.createObjectURL(blob);
      wpUploaded=true; setWallpaperImg(url); return; }
  }catch{}
  // 2. bundled default shipped in EfbWeb/ (wallpaper.jpg | .png), if present
  for(const f of ['wallpaper.jpg','wallpaper.png']){
    try{ const r=await fetch('/'+f,{method:'HEAD'}); if(r.ok){ setWallpaperImg('/'+f); return; } }catch{}
  }
  // 3. built-in gradient
  document.documentElement.removeAttribute('data-wallpaper');
}
function setWallpaperImg(url){
  document.documentElement.style.setProperty('--wallpaper-img',`url('${url}')`);
  document.documentElement.dataset.wallpaper='custom';
}

// ═══ LAUNCHER ══════════════════════════════════════════════
// Small line-art SVG set (24x24, currentColor stroke) — no emoji, so every
// icon reads the same weight/style regardless of platform or font.
const SVG_ATTRS='viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"';
const ICONS={
  flight:  `<path d="M22 16.5 13 3l-2 4-5-1L2 9l5 2-1 3 4 1 1 5 5-3z"/>`,
  dep:     `<path d="M4 19h16"/><path d="M6 15 12 5l3 5.5"/><path d="M12 5v14"/>`,
  arr:     `<path d="M4 19h16"/><path d="M6 9l6 10 3-5.5"/><path d="M12 19V5"/>`,
  atis:    `<path d="M5 12a7 7 0 0 1 14 0"/><path d="M8.5 12a3.5 3.5 0 0 1 7 0"/><circle cx="12" cy="12" r="1.4" fill="currentColor" stroke="none"/>`,
  map:     `<path d="M9 3 4 5v16l5-2 6 2 5-2V3l-5 2-6-2Z"/><path d="M9 3v16M15 5v16"/>`,
  msg:     `<path d="M4 5h16v11H8l-4 4V5Z"/>`,
  ofp:     `<path d="M6 3h9l4 4v14H6z"/><path d="M15 3v4h4"/><path d="M9 12h6M9 15.5h6M9 8.5h3"/>`,
  chk:     `<rect x="4" y="4" width="16" height="16" rx="2.5"/><path d="M8 12.5l2.4 2.4L16 9.5"/>`,
  land:    `<circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="4"/><circle cx="12" cy="12" r=".9" fill="currentColor" stroke="none"/>`,
  scr:     `<path d="M4 20l1-4.2L15.6 5.2a1.6 1.6 0 0 1 2.2 0l1 1a1.6 1.6 0 0 1 0 2.2L8.2 19 4 20Z"/>`,
  man:     `<path d="M4 5.5C4 4.7 4.7 4 5.5 4H12v16H5.5A1.5 1.5 0 0 1 4 18.5Z"/><path d="M20 5.5c0-.8-.7-1.5-1.5-1.5H12v16h6.5a1.5 1.5 0 0 0 1.5-1.5Z"/>`,
  game:    `<rect x="3" y="8" width="18" height="9" rx="4.5"/><path d="M8 10.5v4M6 12.5h4"/><circle cx="16" cy="11" r="1" fill="currentColor" stroke="none"/><circle cx="18.5" cy="13.5" r="1" fill="currentColor" stroke="none"/>`,
  set:     `<circle cx="12" cy="12" r="3"/><path d="M19.4 13.5a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.9 2.9l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.9-2.9l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.6-1H4a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.9-2.9l.1.1a1.7 1.7 0 0 0 1.9.3H10a1.7 1.7 0 0 0 1-1.6V4a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.9 2.9l-.1.1a1.7 1.7 0 0 0-.3 1.9V10a1.7 1.7 0 0 0 1.6 1H20a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z"/>`,
  acars:   `<path d="M12 20V10M12 10 7 6M12 10l5-4"/><path d="M4 20h16"/>`,
};
// Springboard icon art — full-bleed 64×64 illustrations, one per app, drawn in the
// iPad/Fenix-EFB idiom: a dark tile with a single vivid glyph (Settings is the one
// metallic exception, as on iOS). Gradient/mask ids are prefixed per app so the
// inline SVGs never collide. The small line-art ICONS set is kept for in-app controls.
// Top-view airliner, nose up, centred on 0,0 (±18 span, ±20 length).
const PLANE='M0-20c1.6 0 2.4 2 2.4 4.5V-4L18 4v3L2.4 3.5V12L8 16.5v2l-6.8-1.7L0 19.5l-1.2-2.7L-8 18.5v-2l5.6-4.5V3.5L-18 7V4l15.6-8v-11.5C-2.4-18-1.6-20 0-20Z'
  +'M6.2-3.6h2.6V2a1.3 1.3 0 0 1-2.6 0ZM-8.8-3.6h2.6V2a1.3 1.3 0 0 1-2.6 0Z';
const lg=(id,a,b,x2=0,y2=1)=>`<linearGradient id="${id}" x1="0" y1="0" x2="${x2}" y2="${y2}"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient>`;
const rg=(id,a,b,cx='50%',cy='35%',r='75%')=>`<radialGradient id="${id}" cx="${cx}" cy="${cy}" r="${r}"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></radialGradient>`;
function gearPath(cx,cy,ro,ri,n){
  const w=Math.PI/n*.52, p=[];
  for(let i=0;i<n;i++){ const a=i*2*Math.PI/n;
    [[ri,a-w*1.25],[ro,a-w*.7],[ro,a+w*.7],[ri,a+w*1.25]].forEach(([r,t])=>p.push(`${(cx+r*Math.cos(t)).toFixed(2)} ${(cy+r*Math.sin(t)).toFixed(2)}`)); }
  return 'M'+p.join('L')+'Z';
}
const TILE_ART={
  flight:`<defs>${rg('fl-bg','#1c2f55','#05080f','50%','28%','85%')}
      ${rg('fl-gl','rgba(77,155,236,.42)','rgba(77,155,236,0)','50%','55%','50%')}
      ${lg('fl-a','#9dd0ff','#2a68cf')}
      <mask id="fl-m"><image href="mark-a.png" x="19.6" y="12" width="24.8" height="40"/></mask></defs>
    <rect width="64" height="64" fill="url(#fl-bg)"/><rect width="64" height="64" fill="url(#fl-gl)"/>
    <rect width="64" height="64" fill="url(#fl-a)" mask="url(#fl-m)"/>`,
  depperf:`<defs>${lg('dp-bg','#33455a','#141c26')}${lg('dp-gd','#6a8bad','#3b5470',1,1)}</defs>
    <rect width="64" height="64" fill="url(#dp-bg)"/>
    <path d="M0 64V47L64 21V64Z" fill="url(#dp-gd)" opacity=".9"/>
    <path d="M0 47 64 21" stroke="rgba(255,255,255,.18)" stroke-width=".8"/>
    <g transform="translate(35.5 33) rotate(48) scale(.9)"><path d="${PLANE}" fill="#0a1320" opacity=".35"/></g>
    <g transform="translate(33 29) rotate(48) scale(.9)"><path d="${PLANE}" fill="#fff"/></g>`,
  arrperf:`<defs>${lg('ar-bg','#2a2e35','#0b0c0f')}${lg('ar-rw','#34383f','#565b63')}</defs>
    <rect width="64" height="64" fill="url(#ar-bg)"/>
    <path d="M27 8h10l21 56H6Z" fill="url(#ar-rw)"/>
    <path d="M27 8 6 64M37 8l21 56" stroke="#f59e0b" stroke-width="2.3"/>
    <path fill="#ffd23f" d="M31.55 10.5h.9l.12 4h-1.14ZM31.35 18.5h1.3l.25 6h-1.8ZM31.05 30h1.9l.32 8h-2.54ZM30.65 45h2.7l.55 13.5h-3.8Z"/>`,
  map:`<defs>${rg('mp-bg','#16305a','#060d1a')}</defs>
    <rect width="64" height="64" fill="url(#mp-bg)"/>
    <path d="M12.5 17 24 13v34l-11.5 4Z" fill="#1886d6"/><path d="M24 13l16 4v34l-16-4Z" fill="#3cb0ff"/><path d="M40 17l11.5-4v34L40 51Z" fill="#1479c4"/>
    <path d="M17 40.5 27.5 27l8.5 7 11-12.5" fill="none" stroke="#fff" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" stroke-dasharray="3.2 2.2"/>
    <circle cx="17" cy="40.5" r="2" fill="#fff"/><circle cx="47" cy="21.5" r="2" fill="#fff"/>`,
  ofp:`<defs>${rg('pb-bg','#2c2d31','#040405','50%','30%','80%')}${lg('pb-w','#ffffff','#c9ced5')}${lg('pb-v','#3d4046','#0c0d10')}${lg('pb-au','#f5d77a','#a47a18')}</defs>
    <rect width="64" height="64" fill="url(#pb-bg)"/>
    <path d="M9 31C9 21 21 15.5 32 15.5S55 21 55 31c0 3-8 4.5-23 4.5S9 34 9 31Z" fill="url(#pb-w)"/>
    <path d="M13.5 33.8c7.5 2.2 29.5 2.2 37 0l-1 6.7c-6.5 2-28.5 2-35 0Z" fill="#15161a"/>
    <path d="M14 37.2c7 2 29 2 36 0" fill="none" stroke="#c9a227" stroke-width=".9"/>
    <path d="M14.5 40.5c6.5 2 28.5 2 35 0-1 6-8 9-17.5 9s-16.5-3-17.5-9Z" fill="url(#pb-v)"/>
    <path d="M19 43.6c7 1.5 19 1.5 26 0" fill="none" stroke="rgba(255,255,255,.28)" stroke-width=".8" stroke-linecap="round"/>
    <path d="M19.5 25.2 29 23.5l3-2.9 3 2.9 9.5 1.7-9.5 1.5-3 2.9-3-2.9Z" fill="url(#pb-au)"/>
    <circle cx="32" cy="25.2" r="1.5" fill="#1d1d1f"/>`,
  atis:`<defs>${lg('at-bg','#1f5fa8','#0b1d3d')}${lg('at-sun','#ffe08a','#ff9f2e')}${lg('at-cl','#ffffff','#c9d6ea')}</defs>
    <rect width="64" height="64" fill="url(#at-bg)"/>
    <circle cx="40" cy="22" r="10" fill="url(#at-sun)"/>
    <g stroke="#ffd36b" stroke-width="2.2" stroke-linecap="round" opacity=".85"><path d="M40 6.5v3.5M52.5 11.5l-2.4 2.4M56 22h-3.5M27.5 11.5l2.4 2.4"/></g>
    <path d="M18 43h28a8 8 0 0 0 0-16 11 11 0 0 0-21-2.5A8.5 8.5 0 0 0 18 43Z" fill="url(#at-cl)"/>
    <g stroke="#7fc1ff" stroke-width="2.4" stroke-linecap="round"><path d="M24 48l-2 5M32 48l-2 5M40 48l-2 5"/></g>`,
  checklist:`<defs>${lg('ck-bg','#103020','#05100a')}${lg('ck-cl','#c3cad2','#7b858f')}</defs>
    <rect width="64" height="64" fill="url(#ck-bg)"/>
    <rect x="15" y="12" width="34" height="44" rx="4.5" fill="#eef1f4"/>
    <rect x="24" y="8.5" width="16" height="8" rx="2.5" fill="url(#ck-cl)"/><circle cx="32" cy="11.2" r="1.3" fill="#3c434b"/>
    <g fill="none" stroke-linecap="round" stroke-linejoin="round">
      <path d="M19.5 26l2.3 2.3 4.3-4.8M19.5 35l2.3 2.3 4.3-4.8" stroke="#1fbf6b" stroke-width="2.5"/>
      <path d="M30 26.5h14M30 35.5h11M30 44.5h13" stroke="#aab3bd" stroke-width="2.4"/>
      <rect x="19.6" y="41.3" width="6.4" height="6.4" rx="1.5" stroke="#aab3bd" stroke-width="1.6"/></g>`,
  landing:`<defs>${rg('ld-bg','#18243b','#060a13')}</defs>
    <rect width="64" height="64" fill="url(#ld-bg)"/>
    <circle cx="32" cy="36" r="21" fill="#0a101d" stroke="#2c3a52" stroke-width="1.4"/>
    <g fill="none" stroke-width="3.4"><path d="M17 36a15 15 0 0 1 7.5-13" stroke="#22c55e"/><path d="M24.5 23a15 15 0 0 1 15 0" stroke="#f0a02a"/><path d="M39.5 23A15 15 0 0 1 47 36" stroke="#ef4444"/></g>
    <path d="M32 36 20.7 29.5" stroke="#fff" stroke-width="2.1" stroke-linecap="round"/>
    <circle cx="32" cy="36" r="2.7" fill="#fff"/><circle cx="32" cy="36" r="1" fill="#0a101d"/>
    <text x="32" y="50.5" text-anchor="middle" font-family="ui-monospace,SF Mono,Consolas,monospace" font-size="6.4" font-weight="700" fill="#8fa3c2">-120</text>`,
  messages:`<defs>${lg('ms-bg','#183d73','#08152b')}${lg('ms-f','#86c3ff','#3a82de')}</defs>
    <rect width="64" height="64" fill="url(#ms-bg)"/>
    <rect x="27" y="13" width="25" height="17" rx="8.5" fill="#2e5d9c"/><path d="M45 28.5 51 34l-.5-6.5Z" fill="#2e5d9c"/>
    <rect x="11" y="23" width="31" height="22" rx="11" fill="url(#ms-f)"/><path d="M16.5 41.5 12 50l9.5-5Z" fill="#3f88e2"/>
    <g fill="#fff"><circle cx="19.8" cy="34" r="2.2"/><circle cx="26.5" cy="34" r="2.2"/><circle cx="33.2" cy="34" r="2.2"/></g>`,
  acars:`<defs>${lg('ac-bg','#0c2117','#020705')}${rg('ac-gl','rgba(54,227,138,.16)','rgba(54,227,138,0)','50%','50%','60%')}</defs>
    <rect width="64" height="64" fill="url(#ac-bg)"/>
    <rect x="10" y="12" width="44" height="40" rx="4" fill="#03150b" stroke="#1f4a33" stroke-width="1.4"/>
    <rect x="10" y="12" width="44" height="40" rx="4" fill="url(#ac-gl)"/>
    <g fill="#36e38a"><rect x="14" y="16.5" width="15" height="3.2" rx="1"/>
      <g opacity=".82"><rect x="14" y="23.5" width="34" height="2.4" rx="1.2"/><rect x="14" y="28.5" width="26" height="2.4" rx="1.2"/>
      <rect x="14" y="33.5" width="31" height="2.4" rx="1.2"/><rect x="14" y="38.5" width="20" height="2.4" rx="1.2"/><rect x="14" y="43.5" width="27" height="2.4" rx="1.2"/></g></g>
    <rect x="43.5" y="42.8" width="3.2" height="3.8" fill="#a6ffcf"/>`,
  manuals:`<defs>${lg('mn-bg','#2b2635','#0f0d14')}</defs>
    <rect width="64" height="64" fill="url(#mn-bg)"/>
    <rect x="13" y="17" width="9" height="35" rx="1.5" fill="#4D9BEC"/><rect x="23" y="12" width="10.5" height="40" rx="1.5" fill="#e3aa3a"/>
    <rect x="34.5" y="19" width="8" height="33" rx="1.5" fill="#d9544f"/>
    <g transform="rotate(15 51 52)"><rect x="44" y="20" width="8" height="32" rx="1.5" fill="#8b95a8"/><rect x="44" y="24" width="8" height="1.8" fill="rgba(255,255,255,.5)"/><rect x="44" y="46" width="8" height="1.8" fill="rgba(255,255,255,.5)"/></g>
    <g fill="rgba(255,255,255,.55)"><rect x="13" y="21" width="9" height="1.8"/><rect x="13" y="46" width="9" height="1.8"/><rect x="23" y="16.5" width="10.5" height="1.8"/>
      <rect x="25" y="26" width="6.5" height="9" rx="1" opacity=".7"/><rect x="23" y="47" width="10.5" height="1.8"/><rect x="34.5" y="23" width="8" height="1.8"/><rect x="34.5" y="46.5" width="8" height="1.8"/></g>
    <rect x="9.5" y="52" width="45" height="2.8" rx="1.4" fill="#5d566b"/>`,
  scratch:`<defs>${lg('sc-bg','#2d2716','#0f0c06')}</defs>
    <rect width="64" height="64" fill="url(#sc-bg)"/>
    <rect x="13" y="11" width="32" height="43" rx="3" fill="#fbf3cf"/><path d="M16 11h26a3 3 0 0 1 3 3v4.5H13V14a3 3 0 0 1 3-3Z" fill="#f2c94c"/>
    <path d="M17 26.5h24M17 32.5h24M17 38.5h24M17 44.5h24" stroke="#ddd2a4" stroke-width="1"/>
    <path d="M17.5 25.5c2-2.2 3 1 5-1s3 1.1 5-1 2.2 1.2 4 0M17.5 31.5c2-2.2 3 1 5-1s3 1.1 5-1" fill="none" stroke="#3b4a63" stroke-width="1.2" stroke-linecap="round"/>
    <g transform="rotate(38 45 36)"><rect x="42" y="15" width="6" height="3.6" rx="1" fill="#e86a7c"/><rect x="42" y="18.4" width="6" height="2.2" fill="#c9ccd1"/>
      <rect x="42" y="20.6" width="6" height="23.4" fill="#f5a524"/><rect x="44.2" y="20.6" width="1.6" height="23.4" fill="#ffc24d"/>
      <path d="M42 44h6l-3 7.4Z" fill="#f1d7a8"/><path d="M44.1 49.2h1.8L45 51.4Z" fill="#333"/></g>`,
  game:`<defs>${lg('gm-bg','#241a5e','#0a0724')}</defs>
    <rect width="64" height="64" fill="url(#gm-bg)"/>
    <g fill="#fff"><circle cx="12" cy="11" r=".7" opacity=".7"/><circle cx="50" cy="8" r=".6" opacity=".6"/><circle cx="41" cy="16" r=".5" opacity=".5"/><circle cx="24" cy="7" r=".5" opacity=".5"/><circle cx="56" cy="24" r=".6" opacity=".6"/></g>
    <rect y="46" width="64" height="18" fill="#0d0a26"/><path d="M27 46h10l7 18H20Z" fill="#2c2850"/>
    <path d="M30 50h4M29.2 55h5.6M28.2 60.5h7.6" stroke="#8a84b8" stroke-width="1.2"/>
    <path d="M9 17 30 44" stroke="#a78bfa" stroke-width="1.1" stroke-dasharray="2 2.2" stroke-linecap="round"/>
    <g transform="translate(17.5 28) rotate(142) scale(.42)"><path d="${PLANE}" fill="#fff"/></g>
    <g><circle cx="41" cy="43.4" r="1.7" fill="#fff"/><circle cx="45" cy="43.4" r="1.7" fill="#fff"/><circle cx="49" cy="43.4" r="1.7" fill="#ff4d5e"/><circle cx="53" cy="43.4" r="1.7" fill="#ff4d5e"/></g>`,
  settings:`<defs>${lg('st-bg','#dde1e6','#868d96')}${lg('st-g','#545b64','#262a30')}${lg('st-in','#d3d7dc','#9aa1a9')}</defs>
    <rect width="64" height="64" fill="url(#st-bg)"/>
    <path d="${gearPath(32,32,22.5,18.2,14)}" fill="url(#st-g)"/>
    <circle cx="32" cy="32" r="18.4" fill="url(#st-g)"/>
    <circle cx="32" cy="32" r="12.2" fill="url(#st-in)" stroke="#23272c" stroke-width="1.2"/>
    <g transform="translate(32 32.5) rotate(45) scale(.46)"><path d="${PLANE}" fill="#2a2f36"/></g>`,
};
const tileSvg=id=>`<svg viewBox="0 0 64 64" aria-hidden="true">${TILE_ART[id]||''}</svg>`;
// Springboard order mirrors a real EFB home row: the airline's own app first, the two
// performance tools, charts/map, briefing, then everything else, Settings last.
const APPS=[
  {id:'flight',   name:'AviatesAir',  icon:'flight'},
  {id:'depperf',  name:'Departure Perf', icon:'dep'},
  {id:'arrperf',  name:'Arrival Perf',   icon:'arr'},
  {id:'map',      name:'Map',            icon:'map'},
  {id:'ofp',      name:'Pilot Brief',    icon:'ofp'},
  {id:'atis',     name:'Weather',        icon:'atis'},
  {id:'checklist',name:'Checklists',     icon:'chk'},
  {id:'landing',  name:'Landing',        icon:'land'},
  {id:'messages', name:'Messages',       icon:'msg'},
  {id:'acars',    name:'ACARS',          icon:'acars'},
  {id:'manuals',  name:'Manuals',        icon:'man'},
  {id:'scratch',  name:'Scratchpad',     icon:'scr'},
  {id:'game',     name:'Approach!',      icon:'game'},
  {id:'settings', name:'Settings',       icon:'set'},
];
function iconHtml(a,i){ return `<button class="icon" data-app="${a.id}" style="--i:${i}" aria-label="${a.name}">
  <span class="tile-wrap"><span class="tile">${tileSvg(a.id)}</span><span class="badge hidden" data-badge="${a.id}"></span></span>
  <span class="lbl">${a.name}</span></button>`; }
function buildSpringboard(){
  $('#appGrid').innerHTML=APPS.map(iconHtml).join('');
  $$('#appGrid .icon').forEach(el=>el.onclick=()=>openApp(el.dataset.app));
}
function renderBadges(){
  const n=msg.unreadCount||0;
  $$('[data-badge="messages"]').forEach(b=>{ b.textContent=n>99?'99+':n; b.classList.toggle('hidden',n<=0); });
  const a=(acars.d&&acars.d.unread)||0;
  $$('[data-badge="acars"]').forEach(b=>{ b.textContent=a>99?'99+':a; b.classList.toggle('hidden',a<=0); });
  // Company NOTAMs awaiting READ & ACKNOWLEDGE
  const p=(man.idx&&man.idx.pending)||0;
  $$('[data-badge="manuals"]').forEach(b=>{ b.textContent=p; b.classList.toggle('hidden',p<=0); });
}

// ═══ APP HOST ══════════════════════════════════════════════
const APP_META={ flight:'Flight', depperf:'Departure Performance', arrperf:'Arrival Performance',
  atis:'Weather', map:'Map', messages:'Messages', acars:'ACARS', ofp:'Pilot Brief',
  checklist:'Checklists', landing:'Landing Report', scratch:'Scratchpad',
  game:'Approach!', manuals:'Manuals', settings:'Settings' };

// Apps that draw their own full-screen chrome (no shared heading row), like a real
// airline EFB app. Everything else still gets the slim name/route/actions header.
// Apps that open on a photograph run it under the status bar (white status text over it).
const PHOTO_APPS=new Set(['flight','atis','landing']);
const FULL_APPS=new Set(['flight','depperf','arrperf','ofp','map','checklist','landing','manuals','scratch','game','atis','messages','acars','settings']);

// iPad-style home indicator: tap it, swipe up from it, or press Esc to go home.
// Replaces the old persistent left rail — each app now owns the whole screen.
function initHomeIndicator(){
  const ind=$('#homeInd'); let y0=null;
  ind.onclick=closeApp;
  ind.onpointerdown=e=>{ y0=e.clientY; ind.setPointerCapture(e.pointerId); };
  ind.onpointermove=e=>{ if(y0!=null&&y0-e.clientY>36){ y0=null; closeApp(); } };
  ind.onpointerup=ind.onpointercancel=()=>{ y0=null; };
  document.addEventListener('keydown',e=>{ if(e.key==='Escape'&&activeApp&&!e.defaultPrevented)closeApp(); });
}

let closeT=0;   // the close animation's timer; a quick reopen must cancel it or it would tear the new app down
function openApp(id){
  clearTimeout(closeT);
  if(appCleanup){appCleanup();appCleanup=null;}
  activeApp=id;
  $('#appName').textContent=APP_META[id]||id;
  $('#appRoute').textContent=appRouteLine();
  $('#appActions').innerHTML='';
  const host=$('#appHost'); host.classList.remove('hidden','closing');
  host.classList.toggle('full',FULL_APPS.has(id));
  host.classList.toggle('photo-top',PHOTO_APPS.has(id)); $('#app').classList.toggle('photo-top',PHOTO_APPS.has(id));
  $('#app').classList.add('in-app');
  renderApp();
}
// The small identity line under every app's name, Jeppesen-header style —
// callsign/route/aircraft when a flight is active, otherwise just the date.
function appRouteLine(){
  const s=state||{};
  if(s.hasActiveFlight&&(s.departureIcao||s.arrivalIcao))
    return `${s.departureIcao||'----'} → ${s.arrivalIcao||'----'}${s.aircraftType?' · '+s.aircraftType:''}`;
  return (s.callsign||s.flightNumber)?String(s.callsign||s.flightNumber):'';
}
function closeApp(){
  if(!activeApp)return;
  const host=$('#appHost'); host.classList.add('closing');
  $('#app').classList.remove('in-app','photo-top');
  closeT=setTimeout(()=>{ host.classList.add('hidden'); if(appCleanup){appCleanup();appCleanup=null;} activeApp=null; },240);
}
function renderApp(){
  const body=$('#appBody'); body.className='app-body'; body.innerHTML='';
  ({flight:renderFlight, depperf:renderDeparture, arrperf:renderArrival,
    atis:renderAtis, map:renderMap, messages:renderMessages, acars:renderAcars, ofp:renderPilotBrief,
    checklist:renderChecklist, landing:renderLanding, scratch:renderScratch,
    game:renderGame, manuals:renderManuals, settings:renderSettings}[activeApp]||(()=>{}))(body);
}

// ═══ LIVE (menu bar readout + flight app) ══════════════════
function renderLive(){
  const s=state||{};
  setConn(ws&&ws.readyState===1);
  document.documentElement.style.setProperty('--phase',s.phaseColor||'var(--accent)');
  $('#mbSim').dataset.state=s.simConnected?'on':'off';
  $('#mbGps').dataset.state=s.simConnected&&(s.latitude||s.longitude)?'on':'off';

  // compact live-flight readout in the menu bar (hidden until a session starts)
  const mb=$('#mbFlight');
  const active=!!(s.callsign||s.flightNumber||s.hasActiveFlight);
  mb.classList.toggle('hidden',!active);
  if(active){
    $('#mbCs').textContent=s.callsign||s.flightNumber||'—';
    const mp=$('#mbPhase'); mp.textContent=(s.phase&&s.phase!=='Unknown'?s.phase:'STANDBY').toUpperCase();
    mp.style.setProperty('--phase',s.phaseColor||'var(--accent-2)');
    $('#mbRoute').textContent=`${s.departureIcao||'----'} → ${s.arrivalIcao||'----'}`;
  }
  if(activeApp)$('#appRoute').textContent=appRouteLine();
  if(activeApp==='flight')updateFlight();
}
function etaText(s){ if(s.eteMinutes>0){const h=Math.floor(s.eteMinutes/60),m=s.eteMinutes%60;return (h?h+'h ':'')+m+'m';} return '—'; }

// ═══ SHARED APP PARTS (ui.css) ═════════════════════════════
// Destination photography from Wikimedia Commons, looked up straight from the tablet when it is
// online (CORS via origin=*) and kept per device for a month. The lead image of the place's
// Wikipedia article, free licences only, JPEGs only (no maps, flags or logos), with the author and
// licence for the credit line. Offline or with no usable image the header keeps the wallpaper.
const PH_KEY='efbPhoto:', PH_TTL=30*864e5, phMem=new Map();
const titleCase=t=>t===t.toUpperCase()?t.toLowerCase().replace(/(^|[\s-])(\p{L})/gu,(m,a,b)=>a+b.toUpperCase()):t;
const phPlace=n=>titleCase(String(n||'').replace(/\b(international|intl|airport|aeropuerto|aéroport|flughafen)\b/gi,'').replace(/\s+/g,' ').trim());
async function photoFor(name){
  const q=phPlace(name); if(!q) return null;
  const key=q.toLowerCase();
  if(phMem.has(key)) return phMem.get(key);
  try{ const c=JSON.parse(localStorage.getItem(PH_KEY+key)||'null'); if(c&&Date.now()-c.t<PH_TTL){ phMem.set(key,c.p); return c.p; } }catch{}
  const job=(async()=>{
    if(!navigator.onLine) return null;
    const wp=`https://en.wikipedia.org/w/api.php?action=query&format=json&origin=*&generator=search&gsrlimit=4&gsrsearch=${encodeURIComponent(q)}`
      +`&prop=pageimages&piprop=thumbnail|name&pithumbsize=1600&pilicense=free`;
    const r=await fetch(wp).then(x=>x.json());
    const pages=Object.values(r.query?.pages||{}).sort((a,b)=>a.index-b.index).filter(p=>p.thumbnail&&/\.jpe?g$/i.test(p.pageimage||''));
    const p=pages[0]; if(!p) return null;
    const file='File:'+p.pageimage, strip=h=>{ const d=document.createElement('div'); d.innerHTML=h||''; return d.textContent.trim().replace(/\s+/g,' '); };
    let artist='', lic='', host='commons.wikimedia.org';
    for(const h of ['commons.wikimedia.org','en.wikipedia.org']){
      try{ const m=await fetch(`https://${h}/w/api.php?action=query&format=json&origin=*&prop=imageinfo&iiprop=extmetadata&iiextmetadatafilter=Artist|LicenseShortName&titles=${encodeURIComponent(file)}`).then(x=>x.json());
        const ii=Object.values(m.query?.pages||{})[0]?.imageinfo?.[0]?.extmetadata; if(ii){ artist=strip(ii.Artist?.value); lic=strip(ii.LicenseShortName?.value); host=h; break; } }catch{}
    }
    return {url:p.thumbnail.source, credit:`Photo: ${artist||'Unknown author'}${lic?', '+lic:''}, Wikimedia Commons`, page:`https://${host}/wiki/${encodeURIComponent(file)}`};
  })().catch(()=>null);
  phMem.set(key,job);
  const res=await job; phMem.set(key,res);
  if(res) try{ localStorage.setItem(PH_KEY+key,JSON.stringify({t:Date.now(),p:res})); }catch{}
  return res;
}
// Puts the photo for `place` into a .hero (fades in once loaded) and fills its credit link.
async function heroPhoto(hero,place){
  if(!hero||hero.dataset.place===place) return; hero.dataset.place=place||'';
  hero.classList.remove('loaded'); const cr=hero.querySelector('.hero-cr'); if(cr){ cr.textContent=''; cr.removeAttribute('href'); }
  const p=await photoFor(place); if(!p||!hero.isConnected||hero.dataset.place!==place) return;
  const img=new Image(); img.onload=()=>{ if(hero.dataset.place!==place) return; hero.style.setProperty('--hero-img',`url("${p.url}")`); hero.classList.add('loaded');
    if(cr){ cr.textContent=p.credit; cr.href=p.page; } }; img.src=p.url;
}
const heroTools=list=>`<div class="tools">${list.map(([id,label,ico])=>`<button class="tb on-photo" id="${id}" aria-label="${label}" title="${label}"><svg viewBox="0 0 24 24">${ico}</svg></button>`).join('')}</div>`;
const tabsHtml=(tabs,cur)=>tabs.map(([id,label,ico])=>`<button class="tab${id===cur?' on':''}" data-tab="${id}" aria-pressed="${id===cur}"><svg viewBox="0 0 24 24">${ico}</svg>${label}</button>`).join('');
const rowHtml=(k,v,sub='')=>`<div class="row"><span class="k">${k}</span><span class="val">${v}${sub?`<small>${sub}</small>`:''}</span></div>`;
const IC={
  plane:'<path d="M21 15.5v-2L13.5 9V4.2a1.5 1.5 0 0 0-3 0V9L3 13.5v2l7.5-2.3V18L8 19.8V21l4-1 4 1v-1.2L13.5 18v-4.8z"/>',
  clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  fuel:'<path d="M5 21V5a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v16M3 21h14M15 9h2a2 2 0 0 1 2 2v6a1.5 1.5 0 0 0 3 0V8l-3-3M8 8h4"/>',
  cloud:'<path d="M7 18h10a4 4 0 0 0 0-8 6 6 0 0 0-11.5 1.5A3.3 3.3 0 0 0 7 18z"/>',
  gauge:'<path d="M4 17a8 8 0 1 1 16 0"/><path d="M12 17l4-5"/><circle cx="12" cy="17" r="1.2"/>',
  refresh:'<path d="M4 12a8 8 0 0 1 14-5.3M20 4v4h-4M20 12a8 8 0 0 1-14 5.3M4 20v-4h4"/>',
  share:'<path d="M12 3v12M7 8l5-5 5 5M5 14v5a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-5"/>',
  chev:'<path d="M9 6l6 6-6 6"/>',
  down:'<path d="M12 4v11M7.5 10.5 12 15l4.5-4.5M5 19h14"/>',
};
const chev=`<svg class="chev" viewBox="0 0 24 24">${IC.chev}</svg>`;
const fmtWl=lb=>lb==null||isNaN(lb)?'—':Math.round(wLb(lb)).toLocaleString('en-US')+' '+WU().toLowerCase();
const fmtN=(v,d=0)=>v==null||isNaN(v)?'—':Number(v).toLocaleString('en-US',{minimumFractionDigits:d,maximumFractionDigits:d});
const flLevel=ft=>ft>=18000?'FL'+String(Math.round(ft/100)).padStart(3,'0'):fmtN(Math.round(ft/10)*10)+' ft';
const minsText=m=>m>=60?`${Math.floor(m/60)} h ${m%60?m%60+' min':''}`.trim():`${m} minute${m===1?'':'s'}`;
// minutes late (+) or early (−) against a schedule, as words and a tone
function lateness(act,sched){ if(!act||!sched) return null; const m=Math.round((new Date(act)-new Date(sched))/60000);
  return Math.abs(m)<3?{t:'On time',c:'tone-ok',m}:m>0?{t:`${m} min late`,c:m>15?'tone-bad':'tone-warn',m}:{t:`${-m} min early`,c:'tone-ok',m}; }
const cityOf=(name,icao)=>phPlace(name)||icao||'';

// ── AVIATESAIR APP ──
// The airline's own app for the flight in hand, laid out like BA's or Emirates' trip screen: a
// photograph of where you're going with one sentence on it, the boarding pass on its edge, and
// widgets for what matters now (where you are, when you arrive, fuel at the far end, the next
// checklist, destination weather, the last landing). Times, fuel, weather and the live
// instruments each get a full page from the tab bar. Built once per tab; updateFlight() repaints
// the live values on every snapshot (the heavier parts about once a second).
const DB_WX_KEY='efbDashWxRaw';
let db={wx:{}, wxAt:0, landing:null, landingFor:null, tab:'flight', mapKey:''};
const FL_TABS=[['flight','My flight',IC.plane],['times','Times',IC.clock],['fuel','Fuel',IC.fuel],['wx','Weather',IC.cloud],['live','Live data',IC.gauge]];
// Which checklists matter right now (flyPad shows the ones for the current phase). Works on the
// library's phase keys so every aircraft set maps the same way (an Airbus has no DESCENT C/L —
// its APPROACH C/L is read in the descent — so the lists fall through to the next key).
function clPhaseKeys(s){
  const p=(s.phase||'').toLowerCase(), landed=!!s.landingUtc;
  return /pushback|taxi/.test(p)&&!landed?['after-start','before-taxi','taxi','before-takeoff']
    : /takeoff|initialclimb|climb/.test(p)?['after-takeoff','cruise','descent']
    : /cruise/.test(p)?['cruise','descent','approach']
    : /topofdescent|descent/.test(p)?['descent','approach','landing']
    : /approach/.test(p)?['approach','landing']
    : /landing|rollout|vacating/.test(p)?['landing','after-landing','shutdown']
    : landed?['after-landing','shutdown','secure'] : ['preflight','before-start','after-start','before-taxi'];
}
function dbPhaseChecklists(s){
  const list=clPhaseKeys(s).flatMap(k=>checklists.filter(c=>c.phaseKey===k));
  const open=list.filter(c=>!c.isComplete);
  return (open.length?open:list).slice(0,2);
}
// METAR colouring, flyPad's airline scheme: cyan ICAO, amber = caution, red = warning,
// grey = remarks/trend. Thresholds: wind/gust ≥25 kt amber, ≥35 red; vis <5 km (3 SM) amber,
// <1.5 km (1 SM) red; BKN/OVC ≤ 900 ft amber, ≤ 400 ft or VV red; TS/FZ/SN/GR/FG/+ red, RA/BR/SH amber.
function colorMetar(m,icao){
  let rmk=false;
  return (m||'').trim().split(/\s+/).map(t=>{
    const e=esc(t); let c='';
    if(t==='RMK'||/^(NOSIG|TEMPO|BECMG)$/.test(t)) rmk=true;
    if(rmk) c='g';
    else if(t===icao||t==='METAR'||t==='SPECI') c=t===icao?'c':'g';
    else { let w;
      if((w=t.match(/^(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?KT$/))){ const v=Math.max(+w[2],+(w[3]||0)); c=v>=35?'r':v>=25?'a':''; }
      else if(/^\d{4}$/.test(t)&&!/Z$/.test(t)){ c=+t<1500?'r':+t<5000?'a':''; }
      else if((w=t.match(/^[PM]?(\d+)(?:\/(\d+))?SM$/))){ const sm=w[2]?+w[1]/+w[2]:+w[1]; c=sm<1?'r':sm<3?'a':''; }
      else if((w=t.match(/^(BKN|OVC)(\d{3})(CB|TCU)?$/))){ const h=+w[2]; c=w[3]||h<=4?'r':h<=9?'a':''; }
      else if(/^VV\d{3}$/.test(t)||/(CB|TCU)$/.test(t)) c='r';
      else if(/^[+-]?(VC)?(TS|FZ|SN|GR|GS|FG|FC|SQ|VA|SS|DS|PL)/.test(t)||/^\+/.test(t)) c='r';
      else if(/^-?(VC)?(RA|DZ|BR|SH|HZ)/.test(t)) c='a';
      else if(/^R\d{2}[LCR]?\//.test(t)) c='a';
    }
    return c?`<span class="m-${c}">${e}</span>`:e;
  }).join(' ');
}
async function dbLoadWx(force){
  if(!force&&Date.now()-db.wxAt<300000) return;
  db.wxAt=Date.now();
  const s=state||{}, ids=[plan.departureIcao||s.departureIcao, plan.arrivalIcao||s.arrivalIcao, plan.alternateIcao||s.alternateIcao].filter(Boolean);
  await Promise.all([...new Set(ids)].map(async id=>{ try{ const r=await getJSON('/efb/api/weather?icao='+id); if(r.metar) db.wx[id]={metar:r.metar.replace(/^(METAR|SPECI)\s+/,''),taf:r.taf||'',at:new Date().toISOString()}; }catch{} }));
  if(activeApp==='flight') updateFlight(true);
}
async function dbLoadLanding(){
  try{ const r=await getJSON('/efb/api/landings'); db.landing=r.hasLanding?r.latest:null; }catch{}
  if(activeApp==='flight') updateFlight(true);
}
const flMetar=icao=>{ const live=db.wx[icao]; const ofp=icao===(plan.arrivalIcao)?plan.arrivalMetar:icao===plan.departureIcao?plan.departureMetar:'';
  return {m:(live?.metar||ofp||'').replace(/^(METAR|SPECI)\s+/,''), live:!!live, at:live?.at, taf:live?.taf||''}; };
// plain-language weather from a METAR: "Clear, wind 060 at 10 kt" / "Overcast 600 ft, rain"
function wxWords(m){
  if(!m) return {head:'No report',line:''};
  const x=parseMetar(m), c=wxCategory(m), u=' '+m.toUpperCase()+' ';
  const wx=/ \+?TS/.test(u)?'thunderstorms':/ (\+|-)?(SH)?SN /.test(u)?'snow':/ (\+|-)?(SH|FZ)?(RA|DZ) /.test(u)?(/ -/.test(u)?'light rain':'rain'):/ FG /.test(u)?'fog':/ BR /.test(u)?'mist':'';
  const layer=u.match(/ (FEW|SCT|BKN|OVC)(\d{3})/g)||[], low=layer.map(l=>l.trim()).find(l=>/BKN|OVC/.test(l));
  const first=layer[0]&&layer[0].trim(), cov={FEW:'Few clouds',SCT:'Scattered',BKN:'Broken',OVC:'Overcast'};
  const sky=/CAVOK/.test(u)?'CAVOK':/ (SKC|NSC|CLR|NCD) /.test(u)||!layer.length?'Clear':low?`${cov[low.slice(0,3)]} ${fmtN(+low.slice(3)*100)} ft`:`${cov[first.slice(0,3)]} ${fmtN(+first.slice(3)*100)} ft`;
  const wind=x.windKts==null?'':x.windKts<3?'calm':`${/VRB/.test(m)?'variable':String(x.windDir).padStart(3,'0')+'°'} at ${x.windKts} kt${x.gustKts?`, gusting ${x.gustKts}`:''}`;
  return {head:sky+(wx?', '+wx:''), line:wind?'Wind '+wind:'', temp:x.tempC, qnh:x.qnh, cat:c&&c.cat};
}
function wxIcon(m){
  const u=' '+(m||'').toUpperCase()+' ';
  if(/ \+?TS/.test(u)) return '<svg viewBox="0 0 24 24" fill="none" stroke="#7a86a0" stroke-width="2" stroke-linecap="round"><path d="M7 15h9a4 4 0 0 0 0-8 6 6 0 0 0-11 1.5A3.3 3.3 0 0 0 7 15z" fill="#aab4c6"/><path d="M12 16l-2 4h3l-1.5 3" stroke="#f0a020"/></svg>';
  if(/(RA|DZ|SN|SH)/.test(u)) return '<svg viewBox="0 0 24 24" fill="none" stroke="#2f7fd6" stroke-width="2" stroke-linecap="round"><path d="M7 14h9a4 4 0 0 0 0-8 6 6 0 0 0-11 1.5A3.3 3.3 0 0 0 7 14z" fill="#b9c4d6" stroke="#9aa7bd"/><path d="M8 17l-1 3M12 17l-1 3M16 17l-1 3"/></svg>';
  if(/CAVOK| (SKC|NSC|CLR|NCD) | FEW/.test(u)||!/(BKN|OVC|SCT)/.test(u)) return '<svg viewBox="0 0 24 24" fill="none" stroke="#f0a020" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4.5" fill="#ffc94a"/><path d="M12 2v2.5M12 19.5V22M2 12h2.5M19.5 12H22M4.9 4.9l1.8 1.8M17.3 17.3l1.8 1.8M4.9 19.1l1.8-1.8M17.3 6.7l1.8-1.8"/></svg>';
  return '<svg viewBox="0 0 24 24" fill="none" stroke="#9aa7bd" stroke-width="1.8"><path d="M7 18h10a4 4 0 0 0 0-8 6 6 0 0 0-11.5 1.5A3.3 3.3 0 0 0 7 18z" fill="#c4cddb"/></svg>';
}

function renderFlight(body){
  body.classList.add('nopad');
  body.innerHTML=`<div class="fx" id="fl"><div class="fx-scroll" id="flScroll"></div><nav class="tabs" id="flTabs" aria-label="AviatesAir">${tabsHtml(FL_TABS,db.tab)}</nav></div>`;
  $$('#flTabs .tab').forEach(b=>b.onclick=()=>{ if(db.tab===b.dataset.tab) { $('#flScroll').scrollTop=0; return; } db.tab=b.dataset.tab; db.mapKey='';
    $$('#flTabs .tab').forEach(x=>{ x.classList.toggle('on',x===b); x.setAttribute('aria-pressed',x===b); }); flPage(); });
  flPage();
  dbLoadWx(); dbLoadLanding();
  const iv=setInterval(()=>dbLoadWx(),60000);
  appCleanup=()=>clearInterval(iv);
}
function flPage(){
  const sc=$('#flScroll'); if(!sc) return;
  const s=state||{}, has=plan.hasPlan||s.hasActiveFlight, t=db.tab;
  const tools=heroTools([['flRefresh','Update weather',IC.refresh]]);
  const hero=(compact,title,sub)=>`<header class="hero${compact?' compact':' with-pass'}" id="flHero"><div class="hero-tx"><span class="brand" id="flNo"></span><h1 id="flTitle">${title||''}</h1><p id="flSub">${sub||''}</p></div>${tools}<a class="hero-cr" target="_blank" rel="noopener"></a></header>`;
  if(!has){
    sc.innerHTML=hero(false,'No flight loaded','Book a flight in the desktop app, or import your SimBrief plan here.')
      +`<div class="fx-pad"><div class="w empty" style="margin-top:20px"><b>Import your SimBrief plan</b>Your route, schedule, fuel and weather appear here. This fills the EFB only and doesn't change your ACARS flight.
        <button class="bt pri" id="dbImport"><svg viewBox="0 0 24 24">${IC.down}</svg>Import from SimBrief</button><p class="tone-bad" id="dbImpErr"></p></div></div>`;
    const imp=$('#dbImport'); imp.onclick=async()=>{ imp.disabled=true; imp.lastChild.textContent='Importing…';
      const r=await postJSON('/efb/api/flightplan/import',{username:''}).catch(()=>({error:'Could not reach the desktop app.'}));
      if(!r||r.error){ imp.disabled=false; imp.lastChild.textContent='Import from SimBrief'; $('#dbImpErr').textContent=(r&&r.error)||'Import failed.'; return; }
      plan=r; pb.data=null; flPage(); };
  } else if(t==='flight'){
    sc.innerHTML=hero(false)+`<section class="pass" id="flPass"></section>
      <div class="fx-pad fl-body"><div class="wg">
        <button class="w w2 h2 fl-map" id="flMap" aria-label="Open the map"></button>
        <div class="w w2 navy fl-arr" id="flArr"></div>
        <div class="w fl-fuel" id="flFuel"></div>
        <button class="w tint fl-ck" id="flCk"></button>
        <div class="w w2 fl-wx" id="flWx"></div>
        <div class="w" id="flNext"></div>
        <button class="w" id="flLand"></button>
      </div></div>`;
    $('#flMap').onclick=()=>openApp('map');
    $('#flLand').onclick=()=>openApp('landing');
  } else if(t==='times'){
    sc.innerHTML=hero(true,'Times','All times UTC')+`<div class="fx-pad fl-body"><div class="oooi" id="flOooi"></div>
      <div class="gl-h">Vertical profile</div><div class="w prof" id="flProf"></div>
      <div class="fl-sec"><div><div class="gl-h">Schedule</div><div class="gl" id="flSched"></div></div><div><div class="gl-h">En route</div><div class="gl" id="flEnr"></div></div></div></div>`;
  } else if(t==='fuel'){
    sc.innerHTML=hero(true,'Fuel','Weights in '+(WU()==='LB'?'pounds':'kilograms'))+`<div class="fx-pad fl-body"><div class="wg" id="flFuelW"></div>
      <div class="fl-sec"><div><div class="gl-h">Descent</div><div class="gl" id="flDesc"></div></div><div><div class="gl-h">Plan</div><div class="gl" id="flFplan"></div></div></div></div>`;
  } else if(t==='wx'){
    sc.innerHTML=hero(true,'Weather','Departure, arrival and alternate')+`<div class="fx-pad fl-body" id="flWxAll"></div>`;
  } else {
    sc.innerHTML=hero(true,'Live data','From the simulator')+`<div class="fx-pad fl-body"><div class="live" id="flLive"></div>
      <div class="gl-h">Aircraft</div><div class="cfg" id="flCfg"></div><div class="gl-f" id="flSim"></div></div>`;
  }
  $('#flRefresh').onclick=()=>{ dbLoadWx(true); };
  updateFlight(true);
}

function updateFlight(full){
  const s=state||{}; if(!$('#fl')) return;
  const now=Date.now(); const slow=full||now-(updateFlight._t||0)>1000; if(slow) updateFlight._t=now; else if(db.tab!=='live') return;
  const live=!!s.simConnected, t=db.tab, has=plan.hasPlan||s.hasActiveFlight;
  const dep=plan.departureIcao||s.departureIcao||'', arr=plan.arrivalIcao||s.arrivalIcao||'';
  const depName=plan.departureName||s.departureName||'', arrName=plan.arrivalName||s.arrivalName||'';
  const arrCity=cityOf(arrName,arr);
  const fno=plan.flightNumber||s.flightNumber||s.callsign||'';
  const p=s.phase||'', landed=!!s.landingUtc, air=!!s.takeoffUtc&&!landed;
  const total=s.plannedDistanceNm||plan.plannedDistanceNm||0, rem=s.distanceRemainingNm||0;
  const flown=total&&rem?clamp(total-rem,0,total):landed?total:0, pct=total?clamp(flown/total*100,0,100):(s.progressPct||0);
  const sOut=s.scheduledOutUtc||plan.scheduledDepartureUtc, sIn=s.scheduledInUtc||plan.scheduledArrivalUtc;

  // hero: one sentence for where the flight is
  const hero=$('#flHero');
  if(hero&&has){
    heroPhoto(hero,arrName||arr);
    $('#flNo').textContent=fno;
    if(t==='flight'){
      const crz=plan.cruiseAltitudeFt||s.cruiseAltitudeFt||0, alt=s.altitudeFt||0;
      let title, sub;
      if(s.blockInUtc){ title=`Welcome to ${arrCity}`; sub=`On blocks at ${zt(s.blockInUtc)}${s.landingUtc?`, landed ${zt(s.landingUtc)}`:''}`; }
      else if(landed){ title=`Welcome to ${arrCity}`; sub=`Landed at ${zt(s.landingUtc)}${plan.arrivalRunway?' on runway '+esc(plan.arrivalRunway):''}`; }
      else if(air&&s.eteMinutes>0){ title=`${arrCity} in ${minsText(s.eteMinutes)}`;
        sub=/Approach/.test(p)?`On approach${plan.arrivalRunway?' to runway '+esc(plan.arrivalRunway):''}`
          :/Descent|TopOfDescent/.test(p)?`Descending through ${flLevel(alt)}, landing at ${zt(s.etaUtc)}`
          :/Climb/.test(p)?`Climbing through ${flLevel(alt)}${crz?' to '+flLevel(crz):''}`
          :`Cruising at ${flLevel(alt)}${s.todMinutes>0?`, descent at ${zt(new Date(Date.now()+s.todMinutes*60000).toISOString())}`:''}`; }
      else if(air){ title=`On the way to ${arrCity}`; sub=live?`${flLevel(alt)}, ${fmtN(s.groundKts)} kt`:'Simulator not connected'; }
      else if(s.blockOutUtc){ title=`To ${arrCity}`; sub=`Off blocks at ${zt(s.blockOutUtc)}${plan.departureRunway?`, departing runway ${esc(plan.departureRunway)}`:''}`; }
      else { title=`To ${arrCity}`; sub=`${sOut?`Scheduled off blocks ${zt(sOut)}`:'Ready for departure'}${depName?' from '+esc(phPlace(depName)):''}`; }
      $('#flTitle').textContent=title; $('#flSub').innerHTML=sub;
    }
  }
  if(!has) return;

  if(t==='flight'){
    // boarding pass
    const ps=$('#flPass');
    if(ps){
      const late=lateness(s.blockOutUtc,sOut), cell=(k,v,cls='',extra='')=>`<div><small>${k}</small><b class="${cls}">${v}${extra}</b></div>`;
      const todPct=total&&s.todDistanceNm>0?clamp((flown+s.todDistanceNm)/total*100,0,100):null;
      ps.innerHTML=`<div class="pass-a">
          <div class="prt"><div class="c"><b>${esc(dep||'----')}</b><span>${esc(depName)}</span></div>
            <div class="trk" aria-label="${Math.round(pct)}% flown"><i style="width:${pct}%"></i>${todPct!=null?`<em style="left:${todPct}%" title="Top of descent"></em>`:''}
              ${total?`<small style="left:${clamp(pct,12,88)}%">${fmtN(flown)} of ${fmtN(total)} NM</small>`:''}
              <svg viewBox="-20 -20 40 40" style="left:${pct}%"><g transform="rotate(90)"><path d="${PLANE}"/></g></svg></div>
            <div class="c r"><b>${esc(arr||'----')}</b><span>${esc(arrName)}</span></div></div>
          <div class="pcells">
            ${cell('Out',s.blockOutUtc?zt(s.blockOutUtc).slice(0,4):sOut?zt(sOut).slice(0,4):'—',s.blockOutUtc?'':'est',late&&late.m?`<em class="${late.c}">${late.m>0?'+':'−'}${Math.abs(late.m)}</em>`:'')}
            ${cell('Off',s.takeoffUtc?zt(s.takeoffUtc).slice(0,4):'—')}
            ${cell('On',s.landingUtc?zt(s.landingUtc).slice(0,4):s.etaUtc&&air?zt(s.etaUtc).slice(0,4):'—',s.landingUtc?'':'est')}
            ${cell('In',s.blockInUtc?zt(s.blockInUtc).slice(0,4):'—')}
            ${cell('Runways',`${esc(plan.departureRunway||'—')}, ${esc(plan.arrivalRunway||'—')}`)}
            ${cell('Cruise',(plan.cruiseAltitudeFt||s.cruiseAltitudeFt)?flLevel(plan.cruiseAltitudeFt||s.cruiseAltitudeFt):'—')}
          </div></div>
        <div class="pass-b">${cell('Aircraft',esc(s.aircraftType||plan.aircraftType||'—'))}${cell('Reg',esc(s.registration||plan.registration||'—'))}
          ${cell('Alternate',esc(plan.alternateIcao||s.alternateIcao||'—'))}${cell('Cost index',plan.costIndex??'—')}</div>`;
    }
    flMapPaint();
    // arrival
    const a=$('#flArr');
    if(a){
      const at=s.blockInUtc||s.landingUtc||s.etaUtc, l=lateness(at,sIn);
      a.innerHTML=`<h4>${s.blockInUtc?'Arrived at the gate':landed?'Landed':'Estimated arrival'}</h4>
        <div class="big"><b>${at?zt(at):'—'}</b>${l?`<span class="${l.c}">${l.t}</span>`:''}</div>
        <div class="bar"><i style="width:${pct}%"></i><u style="left:${pct}%"></u></div>
        <div class="ends"><span><b>${esc(dep)}</b>${s.takeoffUtc?'Off '+zt(s.takeoffUtc):sOut?'STD '+zt(sOut):''}</span>
          <span>${air&&s.eteMinutes>0?minsText(s.eteMinutes)+' to go':landed?'Flight complete':''}</span>
          <span><b>${esc(arr)}</b>${sIn?'STA '+zt(sIn):''}</span></div>`;
    }
    // fuel at destination: the gauge is fuel on board now, the line is what's left on arrival
    const f=$('#flFuel');
    if(f){
      const pred=s.predictedLandingFuelLbs, planL=s.plannedLandingFuelLbs||plan.landingFuelLbs, cap=s.fuelPct>0?s.fuelLbs/s.fuelPct:0;
      const diff=pred>0&&planL>0?pred-planL:null, low=s.fuelStatus==='LOW', caut=s.fuelStatus==='CAUTION';
      f.innerHTML=`<h4>Fuel at ${esc(arr||'arrival')}</h4><div class="v ${low?'tone-bad':caut?'tone-warn':''}">${live&&pred>0?fmtN(Math.round(wLb(pred))):'—'}<small>${WU().toLowerCase()}</small></div>
        <p class="${diff==null?'':low?'tone-bad':diff<0?'tone-warn':'tone-ok'}" style="font-weight:600">${diff==null?(live?'Shown once airborne':'Simulator not connected'):low?'Below reserve, check fuel':`${fmtN(Math.abs(Math.round(wLb(diff))))} ${WU().toLowerCase()} ${diff>=0?'above':'below'} plan`}</p>
        <p>${live?`On board ${fmtN(Math.round(wLb(s.fuelLbs)))} ${WU().toLowerCase()}`:''}</p>
        <div class="gauge" title="Fuel on board, line at the estimate for arrival"><i style="height:${live?clamp((s.fuelPct||0)*100,0,100):0}%"></i>${live&&cap&&pred>0?`<u style="bottom:${clamp(pred/cap*100,0,100)}%"></u>`:''}</div>`;
    }
    // next checklist, with the first lines still to read
    const ck=$('#flCk');
    if(ck){
      const c=dbPhaseChecklists(s)[0];
      ck.innerHTML=c?`<h4>${c.isComplete?'Checklist complete':'Next checklist'}<span class="r">${c.checkedCount} of ${c.totalCount}</span></h4><div class="v">${esc(c.name.replace(/\b\w+/g,w=>w[0]+w.slice(1).toLowerCase()))}</div>
        <ol>${c.items.filter(i=>!i.isChecked).slice(0,3).map(i=>`<li><span>${esc(i.challenge)}</span><span>${esc((i.response||'').replace(/_{3,}/g,'…'))}</span></li>`).join('')}</ol>`
        :`<h4>Checklists</h4><div class="v">Open</div><p>Normal checklists for your aircraft.</p>`;
      ck.onclick=()=>{ if(c) activeChecklistId=c.id; openApp('checklist'); };
    }
    // destination weather in words
    const w=$('#flWx');
    if(w){
      const r=flMetar(arr), d=wxWords(r.m);
      w.innerHTML=`<h4>${esc(arrCity||arr)} now${d.cat?` <span class="cat ${d.cat}">${d.cat}</span>`:''}<span class="r">${r.live?'Live '+zt(r.at):r.m?'From the OFP':''}</span></h4>
        <div class="v">${wxIcon(r.m)}${d.temp!=null?d.temp+'°':''}<span style="font-size:20px;font-weight:700;letter-spacing:-.01em">${esc(d.head)}</span></div>
        <p>${esc([d.line,d.qnh?'QNH '+d.qnh:'',plan.arrivalRunway?'runway '+plan.arrivalRunway:''].filter(Boolean).join(', '))}</p>
        ${r.m?`<p class="mono" style="font-size:12px">${esc(r.m)}</p>`:''}`;
    }
    // next waypoint / descent
    const n=$('#flNext');
    if(n){
      n.innerHTML=s.descendNow&&live?`<h4>Descent</h4><div class="v tone-warn">Descend now</div><p>At or past the computed top of descent.</p>`
        :air&&s.nextWaypoint?`<h4>Next waypoint</h4><div class="v">${esc(s.nextWaypoint)}</div><p>${fmtN(s.nextWaypointNm)} NM, at ${zt(s.nextWaypointEtaUtc)}</p>${s.todDistanceNm>0?`<p>Descent in ${fmtN(s.todDistanceNm)} NM</p>`:''}`
        :`<h4>Departure</h4><div class="v">${esc(plan.departureRunway?'RWY '+plan.departureRunway:dep||'—')}</div><p>${esc(plan.sid||'')}${plan.sid&&dep?' from ':''}${esc(dep)}</p>`;
    }
    const ld=$('#flLand');
    if(ld){
      const l=db.landing;
      ld.innerHTML=l?`<h4>Last landing</h4><div class="v" style="color:${esc(l.gradeColor||'inherit')}">${Math.round(l.score||0)}<small>/ 100</small></div>
        <p>${esc(l.airportIcao||'')}${l.runway?' runway '+esc(l.runway):''}, ${Math.round(l.verticalSpeedFpm||0)} fpm</p>`
        :`<h4>Last landing</h4><div class="v">—</div><p>Your first landing report appears here.</p>`;
    }
  }
  else if(t==='times'){
    const oo=$('#flOooi');
    if(oo){
      const card=(k,act,sched,sk)=>{ const l=lateness(act,sched);
        return `<div class="w${act?' done':''}"><h4>${k}</h4><div class="v">${act?zt(act):sched?zt(sched):'—'}</div><p>${act?(l?`<span class="${l.c}">${l.t}</span>`:'Actual'):sched?`${sk}, not yet`:'Not yet'}</p></div>`; };
      oo.innerHTML=card('Off blocks',s.blockOutUtc,sOut,'Scheduled')+card('Takeoff',s.takeoffUtc,null)+card('Landing',s.landingUtc,air?s.etaUtc:null,'Estimate')+card('On blocks',s.blockInUtc,sIn,'Scheduled');
    }
    flProfile();
    const blk=s.blockOutUtc?Math.round(((s.blockInUtc?new Date(s.blockInUtc):new Date())-new Date(s.blockOutUtc))/60000):0;
    const airm=s.takeoffUtc?Math.round(((s.landingUtc?new Date(s.landingUtc):new Date())-new Date(s.takeoffUtc))/60000):0;
    const sc=$('#flSched'); if(sc) sc.innerHTML=rowHtml('Scheduled off blocks',sOut?zt(sOut):'—')+rowHtml('Scheduled on blocks',sIn?zt(sIn):'—')
      +rowHtml('Block time',blk?hmm(blk):'—',s.blockInUtc?'':blk?'so far':'')+rowHtml('Air time',airm?hmm(airm):'—',s.landingUtc?'':airm?'so far':'')
      +rowHtml('Planned flight time',plan.plannedFlightTimeMin?hmm(plan.plannedFlightTimeMin):'—');
    const en=$('#flEnr'); if(en) en.innerHTML=rowHtml('Next waypoint',s.nextWaypoint?esc(s.nextWaypoint):'—',s.nextWaypoint?`${fmtN(s.nextWaypointNm)} NM, ${zt(s.nextWaypointEtaUtc)}`:'')
      +rowHtml('Top of descent',s.todDistanceNm>0?fmtN(s.todDistanceNm)+' NM':'—',s.todMinutes>0?`in ${s.todMinutes} min`:'')
      +rowHtml('Time to go',s.eteMinutes>0?hmm(s.eteMinutes):'—')+rowHtml('Estimated landing',air&&s.etaUtc?zt(s.etaUtc):'—')
      +rowHtml('Distance flown',total?fmtN(flown)+' NM':'—',total?`of ${fmtN(total)}`:'');
  }
  else if(t==='fuel'){
    const fw=$('#flFuelW');
    if(fw){
      const pred=s.predictedLandingFuelLbs, planL=s.plannedLandingFuelLbs||plan.landingFuelLbs, u=WU().toLowerCase();
      const w=(k,v,sub,cls='')=>`<div class="w"><h4>${k}</h4><div class="v ${cls}">${v}<small>${u}</small></div><p>${sub}</p></div>`;
      fw.innerHTML=w('On board',live?fmtN(Math.round(wLb(s.fuelLbs))):'—',live?`${Math.round((s.fuelPct||0)*100)}% of capacity`:'Simulator not connected')
        +w('Burning',live?fmtN(Math.round(wLb(s.fuelBurnPph))):'—','per hour')
        +w(`At ${esc(arr||'arrival')}`,live&&pred>0?fmtN(Math.round(wLb(pred))):'—',s.fuelStatus?`Status ${esc(s.fuelStatus.toLowerCase())}`:'Estimate',s.fuelStatus==='LOW'?'tone-bad':s.fuelStatus==='CAUTION'?'tone-warn':'tone-ok')
        +w('Planned at arrival',planL>0?fmtN(Math.round(wLb(planL))):'—','From the OFP');
    }
    const ds=$('#flDesc'); if(ds) ds.innerHTML=rowHtml('Top of descent',s.descendNow&&live?'<span class="tone-warn">Descend now</span>':s.todDistanceNm>0?fmtN(s.todDistanceNm)+' NM':'—',s.todMinutes>0?`in ${s.todMinutes} min`:'')
      +rowHtml('Required descent',s.requiredDescentFtPerNm>0?fmtN(s.requiredDescentFtPerNm)+' ft/NM':'—')+rowHtml('Cruise',(plan.cruiseAltitudeFt||s.cruiseAltitudeFt)?flLevel(plan.cruiseAltitudeFt||s.cruiseAltitudeFt):'—');
    const fp=$('#flFplan'); if(fp) fp.innerHTML=rowHtml('Ramp fuel',plan.rampFuelLbs>0?fmtWl(plan.rampFuelLbs):'—')+rowHtml('Landing fuel',plan.landingFuelLbs>0?fmtWl(plan.landingFuelLbs):'—')
      +rowHtml('Takeoff weight',plan.takeoffWeightKg>0?bigFromKg(plan.takeoffWeightKg).toFixed(1)+' '+BWU():'—')+rowHtml('Zero fuel weight',plan.zeroFuelWeightKg>0?bigFromKg(plan.zeroFuelWeightKg).toFixed(1)+' '+BWU():'—');
  }
  else if(t==='wx'){
    const el=$('#flWxAll');
    if(el){
      let raw=false; try{ raw=localStorage.getItem(DB_WX_KEY)==='1'; }catch{}
      const ids=[['Departure',dep,depName],['Arrival',arr,arrName],['Alternate',plan.alternateIcao||s.alternateIcao,'']].filter(x=>x[1]);
      el.innerHTML=`<div class="sg" style="max-width:300px;margin-bottom:6px"><button data-raw="0" class="${raw?'':'on'}">Decoded</button><button data-raw="1" class="${raw?'on':''}">Raw</button></div>`
        +ids.map(([role,icao,name])=>{ const r=flMetar(icao), d=wxWords(r.m);
        return `<div class="gl-h">${role}, ${esc(icao)}${name?' '+esc(phPlace(name)):''}${d.cat?` <span class="cat ${d.cat}">${d.cat}</span>`:''}</div>`
          +(raw?`<div class="gl"><div class="metar-raw">${r.m?colorMetar(r.m,icao):'No METAR available.'}${r.taf?`<br><br>${esc(r.taf)}`:''}</div></div>`
          :`<div class="gl">${rowHtml('Sky',esc(d.head))}${rowHtml('Wind',esc((d.line||'').replace(/^Wind /,'')||'—'))}${rowHtml('Temperature',d.temp!=null?d.temp+'°C':'—')}${rowHtml('QNH',d.qnh?d.qnh+' hPa':'—')}
            ${rowHtml('Report',r.live?'Live, '+zt(r.at):r.m?'From the OFP':'None')}</div>`);}).join('');
      $$('#flWxAll [data-raw]').forEach(b=>b.onclick=()=>{ try{ localStorage.setItem(DB_WX_KEY,b.dataset.raw); }catch{} updateFlight(true); });
    }
  }
  else if(t==='live'){
    const lv=$('#flLive');
    if(lv){
      const w=(k,v,u,cls='')=>`<div class="w"><h4>${k}</h4><div class="v ${cls}">${live?v:'—'}${live&&u?`<small>${u}</small>`:''}</div></div>`;
      const vs=Math.round(s.verticalSpeedFpm||0);
      lv.innerHTML=w('Indicated',fmtN(s.iasKts),'kt')+w('Ground speed',fmtN(s.groundKts),'kt')+w('Altitude',flLevel(s.altitudeFt||0),'','tone-blue')
        +w('Vertical speed',(vs>0?'+':'')+fmtN(vs),'fpm',Math.abs(vs)>3000?'tone-warn':'')+w('Mach',s.mach>.1?'.'+String(Math.round(s.mach*1000)).padStart(3,'0').slice(-3):'—','')
        +w('Heading',String(Math.round(s.headingDeg||0)).padStart(3,'0')+'°','')+w('Track',String(Math.round(s.trackDeg||0)).padStart(3,'0')+'°','')
        +w('Wind',`${String(Math.round(s.windDirDeg||0)).padStart(3,'0')}/${Math.round(s.windKts||0)}`,'kt')+w('Outside air',fmtN(s.oatC),'°C')+w('QNH',fmtN(s.qnhMb),'hPa');
      const c=(k,v,on)=>`<div class="w${on&&live?' on':''}"><h4>${k}</h4><div class="v">${live?v:'—'}</div></div>`;
      $('#flCfg').innerHTML=c('Gear',s.gear==='DN'||s.gear==='DOWN'?'Down':'Up',s.gear==='DN'||s.gear==='DOWN')+c('Flaps',fmtN(s.flapsPct)+'%',(s.flapsPct||0)>1)
        +c('Spoilers',fmtN(s.spoilersPct)+'%',(s.spoilersPct||0)>5)+c('Autopilot',esc(s.apMaster?(s.apModes||'On'):'Off'),s.apMaster);
      $('#flSim').textContent=live?`${fmtN(s.altitudeAglFt)} ft above ground, squawk ${s.squawk||'----'}`:'Simulator not connected.';
    }
  }
  if(slow&&s.landingUtc&&db.landingFor!==s.landingUtc){ db.landingFor=s.landingUtc; dbLoadLanding(); }
}

// Vertical profile: the planned trapezoid (climb ~4.5 nm/1000 ft, the 3:1 + 10 nm descent the
// server's T/D uses) with T/C, T/D and the aircraft plotted at distance flown / altitude.
function flProfile(){
  const el=$('#flProf'); if(!el)return;
  const s=state||{}, total=s.plannedDistanceNm||plan.plannedDistanceNm||0, crz=plan.cruiseAltitudeFt||s.cruiseAltitudeFt||0;
  if(!total||!crz){ el.innerHTML=`<p>The profile shows with a flight plan loaded.</p>`; return; }
  const W=Math.max(300,Math.round(el.clientWidth-36||640)),H=150,L=62,R=10,T=14,B=24, x=d=>L+(d/total)*(W-L-R), top=Math.max(crz*1.12,(s.altitudeFt||0)*1.05,10000), y=a=>T+(1-a/top)*(H-T-B);
  const climb=Math.min(crz/1000*4.5,total*.42), desc=Math.min(crz/1000*3+10,total*.45);
  const pts=[[0,0],[climb,crz],[total-desc,crz],[total,0]];
  const path=pts.map((p,i)=>(i?'L':'M')+x(p[0]).toFixed(1)+' '+y(p[1]).toFixed(1)).join(' ');
  const flown=s.distanceRemainingNm?clamp(total-s.distanceRemainingNm,0,total):0;
  const grid=[0.25,.5,.75,1].map(f=>Math.round(top*f/5000)*5000).filter((v,i,a)=>v>0&&a.indexOf(v)===i);
  el.innerHTML=`<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Vertical profile">
    ${grid.map(a=>`<line x1="${L}" x2="${W-R}" y1="${y(a)}" y2="${y(a)}" class="g"/><text x="${L-6}" y="${y(a)+4}" class="lb" text-anchor="end">${flLevel(a)}</text>`).join('')}
    <path d="${path} L${x(total)} ${y(0)} L${x(0)} ${y(0)} Z" class="ar"/><path d="${path}" class="pl"/>
    ${[['Top of climb',climb],['Top of descent',total-desc]].map(([k,d])=>`<line x1="${x(d)}" x2="${x(d)}" y1="${y(crz)-6}" y2="${H-B}" class="mk"/><text x="${x(d)}" y="${H-B+16}" class="lb" text-anchor="middle">${k}</text>`).join('')}
    <text x="${x(0)}" y="${H-B+16}" class="lb">${esc(plan.departureIcao||'')}</text><text x="${x(total)}" y="${H-B+16}" class="lb" text-anchor="end">${esc(plan.arrivalIcao||'')}</text>
    ${s.simConnected?`<circle cx="${x(flown)}" cy="${y(s.altitudeFt||0)}" r="6" class="own"/>`:''}
  </svg>`;
}

// The map widget: Esri basemap tiles framed on the planned route (online), the route and the
// aircraft drawn over them (always, so offline still shows the shape of the flight).
function flMapPaint(){
  const el=$('#flMap'); if(!el) return;
  const s=state||{}, wps=(plan.waypoints||[]).filter(w=>w.latitude||w.longitude);
  const W=el.clientWidth, H=el.clientHeight; if(!W||!H) return;
  const chip=`<div class="chip">${s.simConnected&&s.takeoffUtc&&!s.landingUtc?`${/Climb/.test(s.phase||'')?'Climbing':/Desc|Approach/.test(s.phase||'')?'Descending':'Cruising'} at ${flLevel(s.altitudeFt||0)}<small>${fmtN(s.groundKts)} kt${s.nextWaypoint?`, ${esc(s.nextWaypoint)} in ${fmtN(s.nextWaypointNm)} NM`:''}</small>`
    :`${esc(plan.departureIcao||'----')} to ${esc(plan.arrivalIcao||'----')}<small>${plan.plannedDistanceNm?fmtN(plan.plannedDistanceNm)+' NM':'Planned route'}</small>`}</div>`;
  if(wps.length<2){ el.innerHTML=`<div class="tiles"></div>${chip}<p style="position:absolute;left:18px;bottom:16px">The route shows with a flight plan loaded.</p>`; return; }
  const dark=document.documentElement.dataset.mode!=='light';
  const mx=lon=>(lon+180)/360*256, my=lat=>{ const r=clamp(lat,-85,85)*Math.PI/180; return (1-Math.log(Math.tan(r)+1/Math.cos(r))/Math.PI)/2*256; };
  const xs=wps.map(w=>mx(w.longitude)), ys=wps.map(w=>my(w.latitude));
  const x0=Math.min(...xs),x1=Math.max(...xs),y0=Math.min(...ys),y1=Math.max(...ys);
  const padX=60,padTop=70,padBot=36, sc=Math.min((W-2*padX)/Math.max(x1-x0,.0001),(H-padTop-padBot)/Math.max(y1-y0,.0001));
  const z=clamp(Math.floor(Math.log2(sc)),2,10), k=sc/2**z, ts=256*k;
  const ox=W/2-(x0+x1)/2*sc, oy=padTop+(H-padTop-padBot)/2-(y0+y1)/2*sc;
  const P=(lat,lon)=>[ox+mx(lon)*sc, oy+my(lat)*sc];
  const key=[W,H,wps.length,wps[0].ident,wps[wps.length-1].ident,dark].join('|');
  if(db.mapKey!==key){
    db.mapKey=key;
    let t=''; const style=dark?'Dark':'Light';
    for(let tx=Math.floor(-ox/ts); tx<=Math.floor((W-ox)/ts); tx++) for(let ty=Math.floor(-oy/ts); ty<=Math.floor((H-oy)/ts); ty++){
      if(ty<0||ty>=2**z) continue; const wx=((tx%2**z)+2**z)%2**z;
      t+=`<img alt="" src="https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_${style}_Gray_Base/MapServer/tile/${z}/${ty}/${wx}" style="left:${ox+tx*ts}px;top:${oy+ty*ts}px;width:${ts+.6}px;height:${ts+.6}px" onerror="this.remove()">`; }
    el.innerHTML=`<div class="tiles">${t}</div><svg class="rt" width="${W}" height="${H}" style="position:absolute;left:0;top:0"></svg><div class="chipw"></div><span class="attr">Esri</span>`;
  }
  // route: flown part solid up to the aircraft, the rest dotted
  const pts=wps.map(w=>P(w.latitude,w.longitude)), ink=dark?'#e6ebf5':'#0A0D17', blue=dark?'#5aa6f0':'#2f7fd6';
  let ac=null, seg=0;
  if(s.latitude||s.longitude){ ac=P(s.latitude,s.longitude);
    let best=1e9; for(let i=1;i<pts.length;i++){ const [ax,ay]=pts[i-1],[bx,by]=pts[i]; const dx=bx-ax,dy=by-ay, u=clamp(((ac[0]-ax)*dx+(ac[1]-ay)*dy)/(dx*dx+dy*dy||1),0,1);
      const d=Math.hypot(ax+u*dx-ac[0],ay+u*dy-ac[1]); if(d<best){ best=d; seg=i; } } }
  const done=ac?[...pts.slice(0,seg),ac]:[pts[0]], todo=ac?[ac,...pts.slice(seg)]:pts;
  const ang=ac&&s.trackDeg!=null?s.trackDeg:0;
  el.querySelector('.rt').innerHTML=`<polyline points="${todo.map(p=>p.join(',')).join(' ')}" fill="none" stroke="${ink}" stroke-width="2.5" stroke-dasharray="1 6" stroke-linecap="round" opacity=".8"/>
    <polyline points="${done.map(p=>p.join(',')).join(' ')}" fill="none" stroke="${blue}" stroke-width="4" stroke-linejoin="round" stroke-linecap="round"/>
    ${[[pts[0],plan.departureIcao,1],[pts[pts.length-1],plan.arrivalIcao,0]].map(([p,id,left])=>`<circle cx="${p[0]}" cy="${p[1]}" r="6" fill="${dark?'#151b2b':'#fff'}" stroke="${ink}" stroke-width="3"/>
      <text x="${p[0]+(left?-10:10)}" y="${p[1]+18}" text-anchor="${left?'end':'start'}" font-family="-apple-system,Segoe UI,sans-serif" font-weight="700" font-size="13" fill="${ink}" paint-order="stroke" stroke="${dark?'#151b2b':'#fff'}" stroke-width="3">${esc(id||'')}</text>`).join('')}
    ${ac?`<g transform="translate(${ac[0]} ${ac[1]}) rotate(${ang})"><path d="${PLANE}" fill="${blue}" stroke="${dark?'#0A0D17':'#fff'}" stroke-width="1.8"/></g>`:''}`;
  el.querySelector('.chipw').outerHTML=`<div class="chipw">${chip}</div>`;
}

// ── PERFORMANCE HELPERS (shared by Departure / Arrival Perf) ──
// A perf form's defaults come from the OFP. If the plan arrives or changes after the form was
// built (opened before boot finished, or a new SimBrief import) and the pilot hasn't touched
// the form, rebuild it from the new plan rather than keep stale or empty values.
const planKey=()=>[plan.hasPlan,plan.departureIcao,plan.arrivalIcao,plan.takeoffWeightKg,plan.zeroFuelWeightKg,plan.departureRunway,plan.arrivalRunway].join('|');
const acKey=s=>String(s||'').toUpperCase().replace(/[^A-Z0-9]/g,'');
function pickAircraft(t){
  const k=acKey(t); if(!k||!aircraft.length) return '';
  const names=a=>[a.code,...(a.aliases||[])].map(acKey);
  const hit=aircraft.find(a=>names(a).includes(k));
  if(hit) return hit.code;
  let best='',len=0;
  for(const a of aircraft) for(const n of names(a)) if(n.length>=4&&n.length>len&&k.includes(n)){ best=a.code; len=n.length; }
  return best;   // '' = not a type we have data for: the pilot picks one, we never substitute
}
// the aircraft picker: the fleet first, then the other types; empty until the pilot chooses
const acOptions=()=>[['','Select aircraft'],...aircraft.filter(a=>a.fleet!==false).map(a=>[a.code,a.name]),
  ...aircraft.filter(a=>a.fleet===false).map(a=>[a.code,a.name+' (non-fleet)'])];
function rwyHeading(r){ if(!r)return''; const m=String(r).match(/(\d{1,2})/); return m?parseInt(m[1])*10:''; }
// Minimal METAR decode for autofilling performance (wind / temp / QNH).
function parseMetar(m){
  const out={};
  if(!m) return out;
  const p=m.split(/\s+/);
  const w=p.find(x=>/^(\d{3}|VRB)\d{2}(G\d{2})?KT$/.test(x));
  if(w){ const d=w.slice(0,3); out.windDir=d==='VRB'?0:parseInt(d,10); out.windKts=parseInt(w.slice(3,5),10); }
  const t=p.find(x=>/^M?\d{2}\/M?\d{2}$/.test(x));
  if(t){ const[tt,dd]=t.split('/'); out.tempC=parseInt(tt.replace('M','-'),10); out.dewC=parseInt(dd.replace('M','-'),10); }
  const q=p.find(x=>/^Q\d{4}$/.test(x));
  if(q) out.qnh=parseInt(q.slice(1),10);
  return out;
}
// ── ARRIVAL PERF APP ──
// The in-flight landing-performance tool, laid out like the Fenix EFB's Arrival Perf (and
// Airbus Flysmart's landing module): RUNWAY (runway + the GRF runway condition code 6…1),
// WEATHER (refresh / apply METAR), AIRCRAFT CONFIG (landing weight, flaps, autobrake,
// reverser, manual vs autoland, A/THR, minimums), LDG DISTANCE PROCEDURE (in-flight
// failures). Output: the FMS approach page (Airbus MCDU PERF APPR / Boeing CDU APPROACH REF),
// a result per braking mode, and Fenix's vertical runway with LDA · LDR · 1.15 LDR.
let arr=null, arrFailures=null;
const RWYCC=[[6,'DRY'],[5,'GOOD'],[4,'GOOD/MED'],[3,'MED'],[2,'MED/POOR'],[1,'POOR']];
// GRF runway condition from the METAR's present weather (a pilot's first guess; the airport
// report wins). Freezing precip → 2, snow / ice pellets / hail → 3, rain / drizzle → 5.
function rwyccFromMetar(m){ const t=' '+(m||'').toUpperCase()+' ';
  if(/\s[+-]?(FZRA|FZDZ)\s/.test(t)) return 2;
  if(/\s[+-]?(VC)?(SH)?(SN|SG|PL|GS|GR)\s/.test(t)) return 3;
  if(/\s[+-]?(VC)?(SH|TS)?(RA|DZ)\s/.test(t)) return 5;
  return 6; }
function arrDefaults(){
  const s=state||{};
  // in flight, ZFW + the live predicted fuel on arrival beats the OFP's planned landing fuel
  const live=s.hasActiveFlight&&s.predictedLandingFuelLbs>0;
  const lwKg=plan.zeroFuelWeightKg?plan.zeroFuelWeightKg+(live?s.predictedLandingFuelLbs:(plan.landingFuelLbs||0))/LB_KG:0;
  const lwT=lwKg?bigFromKg(lwKg):0;
  const d={ icao:(plan.arrivalIcao||s.arrivalIcao||'').toUpperCase(), rwy:plan.arrivalRunway||'', airport:null, aptErr:'',
    manLen:plan.arrivalRunwayLengthM||'', manHdg:rwyHeading(plan.arrivalRunway)||'', manElev:plan.arrivalElevationFt||'',
    ac:pickAircraft(plan.aircraftType||s.aircraftType), lw:lwT?lwT.toFixed(1):'', lwSrc:lwT?(live?'LIVE EST':'OFP'):'',
    flap:'', brake:'MED', rev:'IDLE', autoland:false, athr:true, minType:'BARO', mins:'',
    wind:'', oat:'', qnh:'', metar:plan.arrivalMetar||'', metarSrc:plan.arrivalMetar?'OFP':'', rwycc:6, rwyccSuggested:null,
    failures:[], failCat:null, result:null, err:'' };
  applyMetar(d,d.metar);
  if(d.metar){ d.rwyccSuggested=rwyccFromMetar(d.metar); d.rwycc=d.rwyccSuggested; }
  return d;
}
function arrRunway(){ return arr.airport?.runways?.find(r=>normRwy(r.ident)===normRwy(arr.rwy))||null; }
function arrRwyData(){ const r=arrRunway();
  if(r) return {ident:r.ident,len:r.lengthM,wid:r.widthM,hdg:r.headingTrueDeg,surf:r.surface,elev:arr.airport.elevationFt};
  return {ident:arr.rwy||'',len:+arr.manLen||0,wid:0,hdg:+arr.manHdg||rwyHeading(arr.rwy)||0,surf:'',elev:+arr.manElev||0}; }
function arrProfile(){ return aircraft.find(a=>a.code===arr.ac)||null; }
const arrAirbus=()=>{ const p=arrProfile(); return !p||p.category==='Airbus'; };
// autobrake names per family; codes are what the calculator takes
const arrTurboprop=()=>(arrProfile()||{}).category==='Turboprop';
const brakeOpts=()=>arrTurboprop()?[['MANUAL','MANUAL']]:arrAirbus()?[['LOW','LO'],['MED','MED'],['MAX','HI'],['MANUAL','MAN']]:[['LOW','2'],['MED','3'],['MAX','MAX AUTO'],['MANUAL','MANUAL']];
const brakeName=c=>(brakeOpts().find(o=>o[0]===c)||[c,c])[1];

async function loadArrAirport(icao){
  arr.airport=null; arr.aptErr='';
  if(!/^[A-Z0-9]{4}$/.test(icao)){ if(activeApp==='arrperf') renderArrival(); return; }
  arr.aptErr='LOADING…';
  try{ const r=await api('/efb/api/airport?icao='+icao), d=await r.json();
    if(!r.ok||d.error) arr.aptErr=d.error||'No runway data';
    else{ arr.airport=d; arr.aptErr=''; if(!arrRunway()) arr.rwy=(d.runways[0]||{}).ident||''; } }
  catch{ arr.aptErr='Runway database offline. Enter the runway data manually.'; }
  if(activeApp==='arrperf') renderArrival();
}

async function renderArrival(body){
  body=body||$('#appBody');
  if(!arr||(!arr.touched&&arr.planKey!==planKey())) arr={...arrDefaults(),planKey:planKey()};
  if(!arrFailures||arrFailures.ac!==arr.ac){
    try{ arrFailures={...await getJSON('/efb/api/perf/landing/failures?type='+encodeURIComponent(arr.ac||'')),ac:arr.ac}; }
    catch{ arrFailures={categories:[],failures:[],ac:arr.ac}; }
    // a failure from another family's QRH doesn't exist on this type
    arr.failures=arr.failures.filter(id=>arrFailures.failures.some(f=>f.id===id));
    if(arr.failCat&&!arrFailures.categories.includes(arr.failCat)) arr.failCat=null;
    if(activeApp!=='arrperf')return; }
  if(arrTurboprop()) arr.brake='MANUAL';
  body.classList.add('nopad');
  const p=arrProfile(), rd=arrRwyData(), comp=windComp(rd.hdg,parseWind(arr.wind));
  if(!arr.flap||!(p?.landingFlaps||[]).includes(arr.flap)) arr.flap=p?.standardLandingFlap||(p?.landingFlaps||[]).slice(-1)[0]||'';
  const F=(label,ctl,cls='')=>`<div class="dp-f ${cls}"><label>${label}</label>${ctl}</div>`;
  const sel=(id,opts,val)=>`<select id="${id}" class="dp-in">${opts.map(o=>{const[v,t]=Array.isArray(o)?o:[o,o];
    return `<option value="${esc(v)}" ${String(v)===String(val)?'selected':''}>${esc(t)}</option>`;}).join('')}</select>`;
  const inp=(id,val,extra='')=>`<input id="${id}" class="dp-in" value="${esc(val)}" ${extra}>`;
  const rwys=arr.airport?.runways||[];
  const cats=arrFailures.categories||[];
  const selIn=c=>arr.failures.filter(id=>(arrFailures.failures.find(f=>f.id===id)||{}).category===c).length;
  body.innerHTML=`
  <div class="dp ar">
    <div class="ar-grid">
      <section class="dp-panel dp-inputs">
        <header class="dp-h"><span>Runway</span><span class="dp-apt">${arr.airport?esc(arr.airport.icao+' · '+arr.airport.name):arr.aptErr==='LOADING…'?'Loading runways…':''}</span></header>
        <div class="dp-fields">
          ${F('ICAO Code',inp('arIcao',arr.icao,'maxlength="4" autocapitalize="characters" spellcheck="false" placeholder="LSZH"'))}
          ${F('Runway',rwys.length?sel('arRwy',rwys.map(r=>[r.ident,r.ident]),arr.rwy):inp('arRwyTxt',arr.rwy,'maxlength="3" placeholder="14"'))}
          ${F('Minimums (ft)',`<div class="ar-mins">${sel('arMinT',[['BARO','BARO'],['RADIO','RADIO']],arr.minType)}${inp('arMins',arr.mins,'inputmode="numeric" placeholder="200"')}</div>`,'w2')}
        </div>
        <div class="dp-fields dp-manual ${rwys.length||arr.aptErr==='LOADING…'?'hidden':''}">
          ${F('LDA (m)',inp('arManLen',arr.manLen,'inputmode="numeric"'))}
          ${F('Rwy Hdg (°T)',inp('arManHdg',arr.manHdg,'inputmode="numeric"'))}
          ${F('Elevation (ft)',inp('arManElev',arr.manElev,'inputmode="numeric"'))}
          <div class="dp-note">${arr.aptErr&&arr.aptErr!=='LOADING…'?esc(arr.aptErr):''}</div>
        </div>
        <div class="ar-cc" role="radiogroup" aria-label="Runway condition code">${RWYCC.map(([n,l])=>`<button type="button" role="radio" aria-checked="${arr.rwycc===n}" data-cc="${n}" class="cc${n} ${arr.rwycc===n?'on':''}">
          <b>${n}</b><span>${l}</span>${arr.rwyccSuggested===n?'<i title="Suggested from METAR">METAR</i>':''}</button>`).join('')}</div>

        <header class="dp-h"><button class="dp-sync ar-refresh" id="arRefresh" type="button"><svg ${SVG_ATTRS}><path d="M20 11a8 8 0 0 0-14.3-4.9L4 8"/><path d="M4 3v5h5"/></svg>Refresh METAR</button>
          <span>Weather</span><button class="dp-sync" id="arApply" type="button">Apply METAR</button></header>
        <div class="dp-fields">
          ${F('Wind (°/kt)',inp('arWind',arr.wind,'placeholder="230/11" spellcheck="false"'))}
          ${F('Temperature (°C)',`<div class="dp-pm"><button type="button" id="arSign" aria-label="Toggle sign">±</button>${inp('arOat',arr.oat,'inputmode="numeric" placeholder="15"')}</div>`)}
          ${F('QNH (hPa)',inp('arQnh',arr.qnh,'inputmode="numeric" placeholder="1013"'))}
          ${F('Rwy Components',`<output class="dp-in dp-ro" id="arComp">${fmtComp(comp)}</output>`)}
        </div>

        <header class="dp-h"><span>Aircraft Config</span>${arr.lwSrc?`<span class="dp-apt">LDG WT: ${esc(arr.lwSrc)}</span>`:''}</header>
        <div class="dp-fields">
          ${F('Aircraft',sel('arAc',acOptions(),arr.ac),arr.ac?'w2':'w2 req')}
          ${F(`Landing WT (${BWU()})`,inp('arLw',arr.lw,`inputmode="decimal" placeholder="${WU()==='LB'?'134.0':'60.8'}"`),arr.lw?'':'req')}
          ${F('Flaps',sel('arFlap',(p?.landingFlaps||[]).map(f=>[f,f.replace('Config','CONF').toUpperCase()]),arr.flap))}
          ${F('Autobrake',sel('arBrake',brakeOpts(),arr.brake))}
          ${F('Reverser',sel('arRev',[['MAX','MAX REV'],['IDLE','IDLE REV'],['NONE','NO REV']],arr.rev))}
          ${F('Landing',sel('arLand',[['0','MANUAL'],['1','AUTOLAND']],arr.autoland?'1':'0'))}
          ${F('A/THR',sel('arAthr',[['1','ON'],['0','OFF']],arr.athr?'1':'0'),arrAirbus()?'':'hidden')}
        </div>
        ${coNote(p,'CN-003')}

        <header class="dp-h"><span>Ldg Distance Procedure</span>${arr.failures.length?`<button class="dp-sync ar-clr" id="arClr" type="button">Clear ${arr.failures.length}</button>`:'<span class="dp-apt">No failures</span>'}</header>
        <div class="ar-cats">${cats.map(c=>`<button type="button" data-cat="${esc(c)}" class="${arr.failCat===c?'open':''} ${selIn(c)?'has':''}">${esc(c)}${selIn(c)?`<i>${selIn(c)}</i>`:''}</button>`).join('')}</div>
        ${arr.failCat?`<div class="ar-fails">${arrFailures.failures.filter(f=>f.category===arr.failCat).map(f=>`<label class="ar-fail">
          <input type="checkbox" data-fail="${esc(f.id)}" ${arr.failures.includes(f.id)?'checked':''}><em></em>
          <span><b>${esc(f.name)}</b><small>×${f.distanceFactor.toFixed(2)} LD${f.deltaVrefKts?` · VREF +${f.deltaVrefKts}`:''}${f.note?' · '+esc(f.note):''}</small></span></label>`).join('')}</div>`:''}
      </section>

      <section class="dp-panel dp-out">
        <div class="fms" id="arFms"></div>
        <div class="dp-tiles" id="arTiles"></div>
        <div class="ar-brk" id="arBrk"></div>
        <div class="dp-warn" id="arWarn"></div>
      </section>

      <section class="dp-panel ar-rwy" id="arRwyViz"></section>
    </div>
    <div class="dp-strip">
      <div class="dp-srow"><span class="k">Live METAR</span><span class="v mono" id="arMetar"></span></div>
      <div class="dp-srow dp-cells" id="arRwyInfo"></div>
    </div>
    <button class="dp-calc" id="arCalc" type="button">Calculate</button>
    <button class="dp-reset" id="arReset" type="button">Reset</button>
  </div>`;

  const on=(id,ev,fn)=>{ const el=$('#'+id); if(el) el.addEventListener(ev,fn); };
  const setv=(k,conv=v=>v)=>e=>{ arr[k]=conv(e.target.value); arr.touched=true; arr.result=null; paintArrOutputs(); };
  ['input','change','click'].forEach(ev=>$('#appBody .dp').addEventListener(ev,e=>{ if(e.target.closest('input,select,button')&&!e.target.closest('#arReset')&&arr) arr.touched=true; }));
  on('arIcao','input',e=>{ const v=e.target.value.toUpperCase().replace(/[^A-Z0-9]/g,''); e.target.value=v;
    if(v!==arr.icao){ arr.icao=v; arr.result=null; if(v.length===4) loadArrAirport(v); } });
  on('arRwy','change',e=>{ arr.rwy=e.target.value; arr.result=null; renderArrival(); });
  on('arRwyTxt','input',setv('rwy',v=>v.toUpperCase()));
  on('arManLen','input',setv('manLen')); on('arManHdg','input',setv('manHdg')); on('arManElev','input',setv('manElev'));
  $$('#appBody [data-cc]').forEach(b=>b.onclick=()=>{ arr.rwycc=+b.dataset.cc; arr.result=null; renderArrival(); });
  on('arWind','input',setv('wind')); on('arOat','input',setv('oat')); on('arQnh','input',setv('qnh'));
  on('arSign','click',()=>{ const o=$('#arOat'), v=o.value.trim(); o.value=v.startsWith('-')?v.slice(1):(v?'-'+v:'-'); arr.oat=o.value; arr.result=null; paintArrOutputs(); o.focus(); });
  on('arAc','change',e=>{ arr.ac=e.target.value; arr.flap=''; arr.result=null; renderArrival(); });
  on('arLw','input',e=>{ arr.lw=e.target.value; arr.lwSrc=''; arr.result=null; paintArrOutputs(); });
  on('arFlap','change',setv('flap')); on('arBrake','change',setv('brake')); on('arRev','change',setv('rev'));
  on('arLand','change',setv('autoland',v=>v==='1')); on('arAthr','change',setv('athr',v=>v==='1'));
  on('arMinT','change',setv('minType')); on('arMins','input',setv('mins',v=>v.replace(/\D/g,'')));
  $$('#appBody [data-cat]').forEach(b=>b.onclick=()=>{ arr.failCat=arr.failCat===b.dataset.cat?null:b.dataset.cat; renderArrival(); });
  $$('#appBody [data-fail]').forEach(i=>i.onchange=()=>{ const id=i.dataset.fail;
    arr.failures=i.checked?[...arr.failures,id]:arr.failures.filter(x=>x!==id); arr.result=null; renderArrival(); });
  on('arClr','click',()=>{ arr.failures=[]; arr.result=null; renderArrival(); });
  on('arRefresh','click',refreshArrMetar);
  on('arApply','click',()=>{ if(!arr.metar){ flash($('#arApply'),'No METAR'); return; }
    applyMetar(arr,arr.metar); arr.rwyccSuggested=rwyccFromMetar(arr.metar); arr.rwycc=arr.rwyccSuggested; arr.result=null; renderArrival(); flash($('#arApply'),'Applied'); });
  on('arCalc','click',calcArrival);
  on('arReset','click',()=>{ arr=null; renderArrival(); });

  paintArrOutputs();
  if(!arr.airport&&!arr.aptErr&&arr.icao.length===4) loadArrAirport(arr.icao);
}

async function refreshArrMetar(){
  const btn=$('#arRefresh'); if(!arr.icao){ flash(btn,'Enter ICAO first'); return; }
  btn.classList.add('busy'); let m='';
  try{ m=((await getJSON('/efb/api/weather?icao='+arr.icao)).metar||'').trim(); }catch{}
  btn.classList.remove('busy');
  if(!m){ flash($('#arRefresh'),'No METAR available'); return; }
  arr.metar=m.replace(/^(METAR|SPECI)\s+/,''); arr.metarSrc='LIVE'; arr.rwyccSuggested=rwyccFromMetar(arr.metar);
  paintArrOutputs(); renderArrival(); flash($('#arRefresh'),'Live METAR loaded. Press Apply to use it.');
}

async function calcArrival(){
  const rd=arrRwyData(), w=parseWind(arr.wind), lw=parseFloat(arr.lw);
  arr.err='';
  if(!arr.ac) arr.err='SELECT AIRCRAFT';
  else if(!rd.len) arr.err='RUNWAY DATA MISSING';
  else if(!(lw>0)) arr.err='ENTER LANDING WEIGHT';
  else if(arr.wind.trim()&&!w) arr.err='FORMAT ERROR';
  if(arr.err){ arr.result=null; paintArrOutputs(); return; }
  const ew=effWind(w,rd.hdg);
  const req={aircraftCode:arr.ac, weightLbs:bigToLb(lw), flapSetting:arr.flap, runwayLengthM:rd.len, runwayElevationFt:rd.elev,
    runwayHeadingDeg:rd.hdg, windDirDeg:ew.dir, windKts:ew.kts, oatC:arr.oat.trim()===''?15:parseFloat(arr.oat), qnhMb:parseFloat(arr.qnh)||1013.25,
    runwaySlopePct:0, runwayCondition:arr.rwycc, autobrake:arr.brake, reverser:arr.rev, autoland:arr.autoland, autoThrust:arr.athr, failures:arr.failures};
  const b=$('#arCalc'); b.classList.add('busy'); b.textContent='Computing…';
  try{ arr.result=await postJSON('/efb/api/perf/landing',req); }catch{ arr.result=null; arr.err='COMPUTATION FAILED'; }
  b.classList.remove('busy'); b.textContent='Calculate';
  paintArrOutputs();
}

function paintArrOutputs(){
  if(!$('#arFms'))return;
  const r=arr.result, rd=arrRwyData(), comp=windComp(rd.hdg,parseWind(arr.wind));
  const c=$('#arComp'); if(c) c.textContent=fmtComp(comp);
  $('#arFms').innerHTML=arrAirbus()?mcduAppr(r,rd):cduApproachRef(r,rd);
  const t=(k,v,cls='')=>`<div class="dp-tile ${cls}"><div class="k">${k}</div><div class="v">${v}</div></div>`;
  const st=!r?'':r.status==='UNSAFE'?'bad':r.status==='CAUTION'?'warn':'good';
  $('#arTiles').innerHTML=t(arrAirbus()?'VAPP (kt)':'VREF+ (kt)',r?r.vapp:'—')+t('LDR (m)',r?r.actualDistanceM.toLocaleString():'—')+
    t('1.15 LDR (m)',r?r.operationalDistanceM.toLocaleString():'—',st)+t('Margin (m)',r?`${r.marginM>=0?'+':''}${Math.round(r.marginM).toLocaleString()}`:'—',st);
  // one result per braking mode (Flysmart shows every autobrake setting side by side)
  const lda=rd.len||0;
  $('#arBrk').innerHTML=r?`<div class="ar-brk-h"><span>Braking</span><span>1.15 LDR vs LDA ${lda?lda.toLocaleString()+' m':''}</span></div>`+
    Object.entries(r.distanceByBrake).map(([k,v])=>{ const pct=lda?clamp(v/lda*100,0,100):0, cls=!lda?'':v>lda?'bad':v>lda*.9?'warn':'good';
      return `<div class="ar-brk-r ${k===r.autobrake?'sel':''}"><b class="mono">${esc(brakeName(k))}</b><i><em class="${cls}" style="width:${pct}%"></em></i><span class="mono ${cls}">${v.toLocaleString()} m</span></div>`; }).join('')
    :`<div class="ar-none">Calculate to compare braking modes.</div>`;
  const warns=r?.warnings||[];
  $('#arWarn').innerHTML=warns.length>1?warns.slice(1).map(w=>`<span>${esc(w)}</span>`).join(''):'';
  $('#arRwyViz').innerHTML=arrRunwayViz(r,rd);
  $('#arMetar').innerHTML=arr.metar?`${arr.metarSrc?`<b>${arr.metarSrc}</b> `:''}${esc(arr.metar)}`:'<span class="dim">Press REFRESH METAR to fetch the current METAR</span>';
  const cell=(k,v)=>`<div class="c"><span class="k">${k}</span><span class="v mono">${v||'—'}</span></div>`, a=arr.airport;
  $('#arRwyInfo').innerHTML=`<span class="k">Runway Info</span>`+cell('Ident',rd.ident)+cell('LDA (m)',rd.len?rd.len.toLocaleString():'')+cell('Width (m)',rd.wid||'')+
    cell('Hdg (°T)',rd.hdg?String(rd.hdg).padStart(3,'0'):'')+cell('Surface',rd.surf)+cell('RWYCC',arr.rwycc+' '+RWYCC.find(x=>x[0]===arr.rwycc)[1])+
    cell('Elev (ft)',rd.elev||rd.elev===0?rd.elev:'')+cell('ARP',a?dms(a.latitude,'N','S')+' '+dms(a.longitude,'E','W'):'');
}

// Airbus MCDU PERF APPR: cyan pilot entries (QNH / TEMP / MAG WIND / TRANS / VAPP / minima),
// green computed speeds (VLS, F / S / O), the selected LDG CONF large and the other small.
function mcduAppr(r,rd){
  const L=(l,c,rr)=>`<div class="m-row s"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const D=(l,c,rr)=>`<div class="m-row"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const box='<span class="am">□□□</span>', dash='<span class="dim">---</span>';
  const w=parseWind(arr.wind), full=/full|40/i.test(arr.flap);
  const scratch=arr.err||(r?.warnings?.[0]||'');
  const cy=v=>`<span class="cy">${v}</span>`;
  return `<div class="fms-screen airbus">
    <div class="m-title">APPR</div>
    ${L(' QNH','FLP RETR','FINAL ')}
    ${D(arr.qnh?cy(esc(arr.qnh)):box,r?`F=<span class="gr">${r.flapRetractKts}</span>`:'F=---',rd.ident?cy('RW'+esc(rd.ident)):dash)}
    ${L(' TEMP','SLT RETR',arr.minType==='BARO'?'BARO ':'MDA ')}
    ${D(arr.oat!==''?cy(esc(arr.oat)+'°'):dash,r?`S=<span class="gr">${r.slatRetractKts}</span>`:'S=---',arr.minType==='BARO'&&arr.mins?cy(esc(arr.mins)):dash)}
    ${L(' MAG WIND','CLEAN',arr.minType==='RADIO'?'RADIO ':'DH ')}
    ${D(w?cy(`${w.vrb?'VRB':String(w.dir).padStart(3,'0')+'°'}/${String(w.kts).padStart(3,'0')}`):box,r?`O=<span class="gr">${r.greenDotKts}</span>`:'O=---',arr.minType==='RADIO'&&arr.mins?cy(esc(arr.mins)):dash)}
    ${L(' TRANS FL','','LDG CONF ')}
    ${D('<span class="dim">-----</span>','',full?'<span class="cy sm">CONF3*</span>':cy('CONF3'))}
    ${L(' VAPP','VLS','')}
    ${D(r?cy(r.vapp):box,r?`<span class="gr">${r.vref}</span>`:dash,full?cy('FULL'):'<span class="cy sm">FULL*</span>')}
    ${L('','','')}
    ${D('<span class="sm">&lt;PREV</span>','','<span class="sm">NEXT&gt;</span>')}
    <div class="m-scratch ${scratch?'am':''}">${esc(scratch.toUpperCase())}</div>
  </div>
  <div class="fms-cap"><span>MCDU · PERF APPR</span><span>${r?esc(r.aircraftName)+(r.appliedFailures.length?` · ${r.appliedFailures.length} FAILURE${r.appliedFailures.length>1?'S':''} · ΔVREF +${r.deltaVrefKts}`:''):''}</span></div>`;
}
// Boeing CDU APPROACH REF: gross weight, flap / VREF, the selected FLAP/SPD and wind correction.
function cduApproachRef(r,rd){
  const L=(l,c,rr)=>`<div class="m-row s"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const D=(l,c,rr)=>`<div class="m-row"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const lw=parseFloat(arr.lw), flapDeg=flapShort(arr.flap);
  const scratch=arr.err||(r?.warnings?.[0]||'');
  return `<div class="fms-screen boeing">
    <div class="m-title">APPROACH REF<span class="m-pg">1/1</span></div>
    ${L(' GROSS WT','FLAPS','VREF')}
    ${D(lw?`<span class="wh">${(bigToLb(lw)/1000).toFixed(1)}</span>`:'<span class="am">□□□.□</span>',`<span class="wh">${esc(flapDeg)}°</span>`,r?`<span class="gr">${r.vref}KT</span>`:'<span class="dim">---KT</span>')}
    ${L(' GA N1','','')}
    ${D('<span class="dim">--.-/--.-%</span>','','')}
    ${L(` ${esc(rd.ident?'RW'+rd.ident:'RW--')}`,'','')}
    ${D(`<span class="wh">${rd.len?Math.round(rd.len*3.28084).toLocaleString()+'FT':'-----FT'}</span>`,'',`<span class="wh">${rd.len?rd.len.toLocaleString()+'M':''}</span>`)}
    ${L('','','FLAP/SPD')}
    ${D('','',r?`<span class="wh">${esc(flapDeg)}/${r.vapp}</span>`:'<span class="am">--/---</span>')}
    ${L('','','WIND CORR')}
    ${D('<span class="wh sm">&lt;INDEX</span>','',r?`<span class="wh">+${r.windCorrectionKts}KT</span>`:'<span class="dim">+--KT</span>')}
    <div class="m-scratch ${scratch?'am':''}">${esc(scratch.toUpperCase())}</div>
  </div>
  <div class="fms-cap"><span>CDU · APPROACH REF</span><span>${r?esc(r.aircraftName):''}</span></div>`;
}

// Fenix-style vertical runway: threshold at the bottom, aiming point at 300 m, the stop
// point at LDR and 1.15 LDR measured from the threshold, coloured by margin.
function arrRunwayViz(r,rd){
  const len=rd.len||0, H=420, W=150, y0=H-44, y1=26, X0=46, X1=104;
  const Y=m=>y0-(len?Math.min(m,len*1.08)/len:0)*(y0-y1);
  const col=!r?'#8a8f98':r.status==='UNSAFE'?'#ef4444':r.status==='CAUTION'?'#f0a02a':'#3ddc84';
  const dashes=[]; for(let y=y0-70;y>y1+24;y-=30) dashes.push(`<rect x="${(X0+X1)/2-1}" y="${y-14}" width="2" height="14" fill="#e9e9e9" opacity=".8"/>`);
  const keys=[]; for(let i=0;i<8;i++) keys.push(`<rect x="${X0+4+i*6.6}" y="${y0-26}" width="4" height="22" fill="#f2f2f2"/>`);
  return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid meet" role="img" aria-label="Runway landing distance">
    <rect x="${X0}" y="${y1}" width="${X1-X0}" height="${y0-y1}" fill="#141517" stroke="#e7e8ea" stroke-width="1.2"/>
    ${keys.join('')}${dashes.join('')}
    <text x="${(X0+X1)/2}" y="${y0+18}" text-anchor="middle" class="rv-id">${esc(rd.ident||'XX')}</text>
    ${len?`<rect x="${X0+6}" y="${Y(300)-8}" width="10" height="16" fill="#f2f2f2" opacity=".85"/><rect x="${X1-16}" y="${Y(300)-8}" width="10" height="16" fill="#f2f2f2" opacity=".85"/>`:''}
    ${r&&len?`<rect x="${X0+1}" y="${Y(r.operationalDistanceM)}" width="${X1-X0-2}" height="${y0-Y(r.operationalDistanceM)}" fill="${col}" opacity=".18"/>
      <line x1="${X0-8}" x2="${X1+8}" y1="${Y(r.actualDistanceM)}" y2="${Y(r.actualDistanceM)}" stroke="${col}" stroke-width="2.5"/>
      <line x1="${X0-8}" x2="${X1+8}" y1="${Y(r.operationalDistanceM)}" y2="${Y(r.operationalDistanceM)}" stroke="${col}" stroke-width="1.6" stroke-dasharray="4 3"/>
      <text x="${X1+10}" y="${Y(r.actualDistanceM)+4}" class="rv-sm" fill="${col}">LDR</text>
      <text x="${X1+10}" y="${Y(r.operationalDistanceM)+4}" class="rv-sm" fill="${col}">×1.15</text>`:''}
    <text x="${(X0+X1)/2}" y="${y1-8}" text-anchor="middle" class="rv-sm">${len?len.toLocaleString()+' m':'Select a runway'}</text>
  </svg>
  <div class="ar-tbl mono"><div><small>LDA (M)</small>${len?len.toLocaleString():'-'}</div><div><small>LDR (M)</small>${r?r.actualDistanceM.toLocaleString():'-'}</div><div><small>1.15LDR (M)</small>${r?r.operationalDistanceM.toLocaleString():'-'}</div></div>`;
}

// ── DEPARTURE PERF APP ──
// A standalone takeoff-performance app in the Fenix / Airbus Flysmart idiom: grouped input
// panels (Airport Info, Aircraft Configuration, Weather Conditions) with OFP/METAR sync
// buttons, an FMS takeoff page (Airbus MCDU PERF TAKE OFF or Boeing CDU TAKEOFF REF,
// by aircraft family) showing exactly what to type into the box, TOPL / wind / margin
// tiles, a runway diagram, and the live METAR + runway-info strip. Runways come from
// /efb/api/airport (real lengths, widths, true headings); inputs persist in `dep` for the
// session so leaving and reopening the app keeps the pilot's work.
let dep=null;
const MT2LB=2204.62;
function depDefaults(){
  const s=state||{};
  const d={ icao:(plan.departureIcao||s.departureIcao||'').toUpperCase(), rwy:plan.departureRunway||'',
    shift:'', surface:'DRY', ac:pickAircraft(plan.aircraftType||s.aircraftType), flap:'OPT', toga:false,
    antiIce:'OFF', packs:true, tow:plan.takeoffWeightKg?bigFromKg(plan.takeoffWeightKg).toFixed(1):'', cg:planCg()||'28.0', ta:plan.departureTransAltFt||'',
    wind:'', oat:'', qnh:'', metar:plan.departureMetar||'', metarSrc:plan.departureMetar?'OFP':'',
    airport:null, aptErr:'', manLen:plan.departureRunwayLengthM||'', manHdg:rwyHeading(plan.departureRunway)||'',
    manElev:plan.departureElevationFt||'', result:null, err:'' };
  applyMetar(d,d.metar);
  return d;
}
function applyMetar(d,m){
  const w=parseMetar(m); if(!m)return;
  if(w.windKts!=null)d.wind=`${/\bVRB\d{2}(G\d{2})?KT\b/.test(m)?'VRB':String(w.windDir||0).padStart(3,'0')}/${String(w.windKts).padStart(2,'0')}`;
  if(w.tempC!=null)d.oat=String(w.tempC);
  if(w.qnh!=null)d.qnh=String(w.qnh);
}
// TOW CG from the loadsheet W&B (same model as the Hoppies ACARS loadsheet); null for types
// without W&B data, in which case the pilot's own entry (default 28.0) stands.
const planCg=()=>plan.macTowPercent!=null?Number(plan.macTowPercent).toFixed(1):'';
const normRwy=r=>String(r||'').toUpperCase().replace(/^RW/,'').replace(/^0(?=\d)/,'');
function depRunway(){ return dep.airport?.runways?.find(r=>normRwy(r.ident)===normRwy(dep.rwy))||null; }
function parseWind(t){ const m=String(t||'').trim().toUpperCase().match(/^(VRB|\d{1,3})\s*[\/ ]\s*(\d{1,3})$/);
  return m?{vrb:m[1]==='VRB',dir:m[1]==='VRB'?0:+m[1],kts:+m[2]}:null; }
// Variable wind, the conservative way perf tools take it: calm at ≤3 kt, otherwise the
// whole speed as a tailwind (returns the dir/kts actually sent to the calculator).
function effWind(w,hdg){ if(!w)return {dir:0,kts:0};
  if(!w.vrb)return {dir:w.dir,kts:w.kts};
  return w.kts<=3?{dir:0,kts:0}:{dir:((hdg||0)+180)%360,kts:w.kts}; }
function depRwyData(){
  const r=depRunway();
  if(r) return {ident:r.ident,len:r.lengthM,wid:r.widthM,hdg:r.headingTrueDeg,surf:r.surface,elev:dep.airport.elevationFt};
  const len=+dep.manLen, hdg=+dep.manHdg;
  return {ident:dep.rwy||'',len:len||0,wid:0,hdg:hdg||rwyHeading(dep.rwy)||0,surf:'',elev:+dep.manElev||0};
}
function windComp(hdg,w){ if(!w||!hdg)return null; const e=effWind(w,hdg), a=(e.dir-hdg)*Math.PI/180;
  return {h:Math.round(e.kts*Math.cos(a)),x:Math.round(e.kts*Math.sin(a))}; }
const fmtComp=c=>c?`${c.h>=0?'H':'T'}${Math.abs(c.h)} / X${Math.abs(c.x)}${c.x?c.x>0?'R':'L':''}`:'—';
// Company NOTAM restrictions for the selected type (the flap lists already honour them).
const coNote=(p,k)=>{ const n=(p?.companyNotices||[]).filter(x=>x.startsWith(k));
  return n.length?`<div class="dp-co">${n.map(x=>`<span><b>COMPANY</b>${esc(x)}</span>`).join('')}</div>`:''; };
const flapShort=l=>{ const m=String(l||'').match(/(\d+(?:\+F)?|Full)/i); return m?m[1].toUpperCase():'—'; };
function acProfile(){ return aircraft.find(a=>a.code===dep.ac)||null; }
function isAirbus(){ const p=acProfile(); return !p||p.category==='Airbus'; }

async function loadDepAirport(icao){
  dep.airport=null; dep.aptErr='';
  if(!/^[A-Z0-9]{4}$/.test(icao)){ if(activeApp==='depperf') renderDeparture(); return; }
  dep.aptErr='LOADING…'; renderDepartureRunwayBits();
  try{
    const r=await api('/efb/api/airport?icao='+icao);
    const d=await r.json();
    if(!r.ok||d.error){ dep.aptErr=d.error||'No runway data'; }
    else{
      dep.airport=d; dep.aptErr='';
      if(!depRunway()) dep.rwy=(d.runways[0]||{}).ident||'';
    }
  }catch{ dep.aptErr='Runway database offline. Enter the runway data manually.'; }
  if(activeApp==='depperf') renderDeparture();
}

function renderDeparture(body){
  body=body||$('#appBody');
  if(!dep||(!dep.touched&&dep.planKey!==planKey())) dep={...depDefaults(),planKey:planKey()};
  body.classList.add('nopad');
  const p=acProfile(), rd=depRwyData(), wind=parseWind(dep.wind), comp=windComp(rd.hdg,wind);
  const F=(label,ctl,cls='')=>`<div class="dp-f ${cls}"><label>${label}</label>${ctl}</div>`;
  const sel=(id,opts,val)=>`<select id="${id}" class="dp-in">${opts.map(o=>{const[v,t]=Array.isArray(o)?o:[o,o];
    return `<option value="${esc(v)}" ${String(v)===String(val)?'selected':''}>${esc(t)}</option>`;}).join('')}</select>`;
  const inp=(id,val,extra='')=>`<input id="${id}" class="dp-in" value="${esc(val)}" ${extra}>`;
  const rwys=dep.airport?.runways||[];
  const rwyCtl=rwys.length
    ? sel('dpRwy',rwys.map(r=>[r.ident,r.ident]),dep.rwy)
    : inp('dpRwyTxt',dep.rwy,'maxlength="3" placeholder="27L" autocapitalize="characters"');
  const flaps=(p?.takeoffFlaps||[]).map(f=>[f,f.replace('Config','CONF').toUpperCase()]);
  body.innerHTML=`
  <div class="dp">
    <div class="dp-grid">
      <section class="dp-panel dp-inputs">
        <header class="dp-h"><span>Airport Info</span><span class="dp-apt" id="dpApt"></span></header>
        <div class="dp-fields c5">
          ${F('ICAO Code',inp('dpIcao',dep.icao,'maxlength="4" autocapitalize="characters" spellcheck="false" placeholder="EGLL"'))}
          ${F('Runway',rwyCtl)}
          ${F('TO Shift (m)',inp('dpShift',dep.shift,'inputmode="numeric" placeholder="0"'))}
          ${F('Runway Surface',sel('dpSurf',['DRY','WET'],dep.surface))}
          ${F('Trans Alt (ft)',inp('dpTa',dep.ta,'inputmode="numeric" placeholder="6000"'))}
        </div>
        <div class="dp-fields dp-manual ${rwys.length||dep.aptErr==='LOADING…'?'hidden':''}">
          ${F('TORA (m)',inp('dpManLen',dep.manLen,'inputmode="numeric"'))}
          ${F('Rwy Hdg (°T)',inp('dpManHdg',dep.manHdg,'inputmode="numeric"'))}
          ${F('Elevation (ft)',inp('dpManElev',dep.manElev,'inputmode="numeric"'))}
          <div class="dp-note" id="dpAptErr"></div>
        </div>
        <header class="dp-h"><span>Aircraft Configuration</span>
          <button class="dp-sync" id="dpSyncLs" type="button">Sync Loadsheet (OFP)</button></header>
        <div class="dp-fields">
          ${F('Aircraft',sel('dpAc',acOptions(),dep.ac),dep.ac?'w2':'w2 req')}
          ${F('Flap Config',sel('dpFlap',[['OPT','OPT'],...flaps],dep.flap))}
          ${F('Force TOGA',sel('dpToga',[['0','NO'],['1','YES']],dep.toga?'1':'0'))}
          ${F('Anti-Ice',sel('dpIce',['OFF','ENG','ENG+WING'],dep.antiIce))}
          ${F('Packs',sel('dpPacks',[['1','ON'],['0','OFF']],dep.packs?'1':'0'))}
          ${F(`TOW (${BWU()})`,inp('dpTow',dep.tow,`inputmode="decimal" placeholder="${WU()==='LB'?'133.6':'60.6'}"`))}
          ${F('MACTOW (%)',inp('dpCg',dep.cg,'inputmode="decimal"'),planCg()?'':'est')}
        </div>
        ${coNote(p,'CN-002')}
        ${p&&!planCg()?`<div class="dp-co"><span><b>MACTOW</b>No weight and balance data for the ${esc(p.name)}. Enter MACTOW from your loadsheet; 28.0 is only a typical value.</span></div>`:''}
        <header class="dp-h"><span>Weather Conditions</span>
          <button class="dp-sync" id="dpSyncWx" type="button"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 11a8 8 0 0 0-14.3-4.9L4 8"/><path d="M4 3v5h5"/><path d="M4 13a8 8 0 0 0 14.3 4.9L20 16"/><path d="M20 21v-5h-5"/></svg>Sync Live WX</button></header>
        <div class="dp-fields">
          ${F('Wind (°/kt)',inp('dpWind',dep.wind,'placeholder="270/10" spellcheck="false"'))}
          ${F('Temperature (°C)',`<div class="dp-pm"><button type="button" id="dpSign" aria-label="Toggle sign">±</button>${inp('dpOat',dep.oat,'inputmode="numeric" placeholder="15"')}</div>`)}
          ${F('QNH (hPa)',inp('dpQnh',dep.qnh,'inputmode="numeric" placeholder="1013"'))}
          ${F('Rwy Components',`<output class="dp-in dp-ro" id="dpComp">${fmtComp(comp)}</output>`)}
        </div>
      </section>

      <section class="dp-panel dp-out">
        <div class="fms" id="dpFms"></div>
        <div class="dp-tiles" id="dpTiles"></div>
        <div class="dp-rwyviz" id="dpViz"></div>
        <div class="dp-warn" id="dpWarn"></div>
      </section>
    </div>

    <div class="dp-strip">
      <div class="dp-srow"><span class="k">Live METAR</span><span class="v mono" id="dpMetar"></span></div>
      <div class="dp-srow dp-cells" id="dpRwyInfo"></div>
    </div>
    <button class="dp-calc" id="dpCalc" type="button">Calculate</button>
    <button class="dp-reset" id="dpReset" type="button">Reset</button>
  </div>`;

  // ── wiring: every edit updates `dep`; anything that changes the runway re-derives the view
  const on=(id,ev,fn)=>{ const el=$('#'+id); if(el) el.addEventListener(ev,fn); };
  const setv=(k,conv=v=>v)=>e=>{ dep[k]=conv(e.target.value); dep.touched=true; dep.result=null; paintDepOutputs(); };
  ['input','change','click'].forEach(ev=>$('#appBody .dp').addEventListener(ev,e=>{ if(e.target.closest('input,select,button')&&!e.target.closest('#dpReset')&&dep) dep.touched=true; }));
  on('dpIcao','input',e=>{ const v=e.target.value.toUpperCase().replace(/[^A-Z0-9]/g,''); e.target.value=v;
    if(v!==dep.icao){ dep.icao=v; dep.result=null; if(v.length===4) loadDepAirport(v); } });
  on('dpRwy','change',e=>{ dep.rwy=e.target.value; dep.result=null; renderDeparture(); });
  on('dpRwyTxt','input',e=>{ dep.rwy=e.target.value.toUpperCase(); dep.result=null; paintDepOutputs(); });
  on('dpShift','input',setv('shift'));
  on('dpSurf','change',setv('surface'));
  on('dpTa','input',e=>{ dep.ta=e.target.value.replace(/\D/g,''); paintDepOutputs(); });
  on('dpManLen','input',setv('manLen')); on('dpManHdg','input',setv('manHdg')); on('dpManElev','input',setv('manElev'));
  on('dpAc','change',e=>{ dep.ac=e.target.value; dep.flap='OPT'; dep.result=null; renderDeparture(); });
  on('dpFlap','change',setv('flap'));
  on('dpToga','change',setv('toga',v=>v==='1'));
  on('dpIce','change',setv('antiIce'));
  on('dpPacks','change',setv('packs',v=>v==='1'));
  on('dpTow','input',setv('tow')); on('dpCg','input',setv('cg'));
  on('dpWind','input',setv('wind')); on('dpOat','input',setv('oat')); on('dpQnh','input',setv('qnh'));
  on('dpSign','click',()=>{ const o=$('#dpOat'); const v=o.value.trim();
    o.value=v.startsWith('-')?v.slice(1):(v?'-'+v:'-'); dep.oat=o.value; dep.result=null; paintDepOutputs(); o.focus(); });
  on('dpSyncLs','click',syncDepLoadsheet);
  on('dpSyncWx','click',syncDepWeather);
  on('dpCalc','click',calcDeparture);
  on('dpReset','click',()=>{ const keepApt=dep.airport&&dep.airport.icao===((plan.departureIcao||'').toUpperCase())?dep.airport:null;
    dep=depDefaults(); dep.airport=keepApt; if(!keepApt&&dep.icao.length===4) loadDepAirport(dep.icao); else renderDeparture(); });

  paintDepOutputs();
  if(!dep.airport&&!dep.aptErr&&dep.icao.length===4) loadDepAirport(dep.icao);
}
// the small bits that change while the airport lookup is in flight
function renderDepartureRunwayBits(){
  const a=$('#dpApt'); if(a) a.textContent=dep.aptErr==='LOADING…'?'Loading runways…':'';
}

function flash(btn,text){ if(!btn)return; const o=btn.innerHTML; btn.classList.add('ok'); btn.textContent=text;
  setTimeout(()=>{ btn.classList.remove('ok'); btn.innerHTML=o; },1400); }
function syncDepLoadsheet(){
  if(!plan.hasPlan){ flash($('#dpSyncLs'),'No OFP loaded'); return; }
  if(plan.takeoffWeightKg) dep.tow=bigFromKg(plan.takeoffWeightKg).toFixed(1);
  if(planCg()) dep.cg=planCg();
  if(plan.departureTransAltFt) dep.ta=plan.departureTransAltFt;
  const code=pickAircraft(plan.aircraftType||state.aircraftType); if(code) dep.ac=code;
  if(plan.departureIcao&&plan.departureIcao.toUpperCase()!==dep.icao){ dep.icao=plan.departureIcao.toUpperCase(); dep.rwy=plan.departureRunway||''; loadDepAirport(dep.icao); }
  else if(plan.departureRunway) dep.rwy=plan.departureRunway;
  dep.result=null; renderDeparture();
  flash($('#dpSyncLs'),planCg()?'Loadsheet synced':'Synced. Enter MACTOW.');
}
async function syncDepWeather(){
  const btn=$('#dpSyncWx'); if(!dep.icao){ flash(btn,'Enter ICAO first'); return; }
  btn.classList.add('busy');
  let m='';
  try{ const d=await getJSON('/efb/api/weather?icao='+dep.icao); m=(d.metar||'').trim(); }catch{}
  btn.classList.remove('busy');
  if(m){ dep.metar=m; dep.metarSrc='LIVE'; }
  else if(plan.departureMetar&&(plan.departureIcao||'').toUpperCase()===dep.icao){ m=dep.metar=plan.departureMetar; dep.metarSrc='OFP'; }
  if(!m){ flash($('#dpSyncWx'),'No METAR available'); return; }
  applyMetar(dep,m); dep.result=null; renderDeparture();
  flash($('#dpSyncWx'),dep.metarSrc==='LIVE'?'Live weather synced':'Offline. Using the OFP METAR.');
}

async function calcDeparture(){
  const rd=depRwyData(), w=parseWind(dep.wind), tow=parseFloat(dep.tow);
  dep.err='';
  if(!dep.ac) dep.err='SELECT AIRCRAFT';
  else if(!rd.len) dep.err='RUNWAY DATA MISSING';
  else if(!(tow>0)) dep.err='ENTER TOW';
  else if(dep.wind.trim()&&!w) dep.err='FORMAT ERROR';
  if(dep.err){ dep.result=null; paintDepOutputs(); return; }
  const req={aircraftCode:dep.ac, weightLbs:bigToLb(tow), flapSetting:dep.flap, runwayLengthM:rd.len,
    runwayElevationFt:rd.elev, runwayHeadingDeg:rd.hdg, windDirDeg:effWind(w,rd.hdg).dir, windKts:effWind(w,rd.hdg).kts,
    oatC:dep.oat.trim()===''?15:parseFloat(dep.oat), qnhMb:parseFloat(dep.qnh)||1013.25, runwaySlopePct:0,
    wetRunway:dep.surface==='WET', forceToga:dep.toga, packsOn:dep.packs, antiIce:dep.antiIce,
    cgPct:parseFloat(dep.cg)||28, takeoffShiftM:parseFloat(dep.shift)||0};
  const b=$('#dpCalc'); b.classList.add('busy'); b.textContent='Computing…';
  try{ dep.result=await postJSON('/efb/api/perf/takeoff',req); dep.result._req=req; dep.result._rwy=rd; }
  catch{ dep.result=null; dep.err='COMPUTATION FAILED'; }
  b.classList.remove('busy'); b.textContent='Calculate';
  paintDepOutputs();
}

// ── outputs: FMS page, tiles, runway diagram, METAR / runway strip
function paintDepOutputs(){
  if(!$('#dpFms'))return;
  const r=dep.result, rd=depRwyData(), comp=windComp(rd.hdg,parseWind(dep.wind));
  $('#dpComp').textContent=fmtComp(comp);
  $('#dpFms').innerHTML=isAirbus()?mcduTakeoff(r,rd):cduTakeoffRef(r,rd);
  const apt=$('#dpApt'); if(apt) apt.textContent=dep.airport?`${dep.airport.icao} · ${dep.airport.name}`:'';
  const ae=$('#dpAptErr'); if(ae) ae.textContent=dep.aptErr&&dep.aptErr!=='LOADING…'?dep.aptErr:'';
  const t=(k,v,cls='')=>`<div class="dp-tile ${cls}"><div class="k">${k}</div><div class="v">${v}</div></div>`;
  const mc=!r?'':r.status==='UNSAFE'?'bad':r.status==='CAUTION'?'warn':'good';
  $('#dpTiles').innerHTML=
    t(`TOPL (${BWU()})`,r?(WU()==='LB'?r.perfLimitedWeightLbs/1000:r.perfLimitedWeightLbs/MT2LB).toFixed(1):'—')+
    t('HW / XW (kt)',r?`${r.headwindKts>=0?'H':'T'}${Math.abs(r.headwindKts)} / ${Math.abs(r.crosswindKts)}`:'—')+
    t('Margin (m)',r?`${r.marginM>=0?'+':''}${Math.round(r.marginM).toLocaleString()}`:'—',mc)+
    t('Limitation',r?r.limitCode:'—');
  $('#dpViz').innerHTML=runwayDiagram(r,rd);
  const warns=r?.warnings||[];
  $('#dpWarn').innerHTML=warns.length>1?warns.slice(1).map(w=>`<span>${esc(w)}</span>`).join(''):'';
  $('#dpMetar').innerHTML=dep.metar?`${dep.metarSrc?`<b>${dep.metarSrc}</b> `:''}${esc(dep.metar)}`:'<span class="dim">Press SYNC LIVE WX to fetch the current METAR</span>';
  const c=(k,v)=>`<div class="c"><span class="k">${k}</span><span class="v mono">${v||'—'}</span></div>`;
  const a=dep.airport;
  $('#dpRwyInfo').innerHTML=`<span class="k">Runway Info</span>`+c('Ident',rd.ident)+c('Length (m)',rd.len?rd.len.toLocaleString():'')+
    c('Width (m)',rd.wid||'')+c('Hdg (°T)',rd.hdg?String(rd.hdg).padStart(3,'0'):'')+c('Surface',rd.surf)+
    c('Elev (ft)',rd.elev||rd.elev===0?rd.elev:'')+c('ARP',a?dms(a.latitude,'N','S')+' '+dms(a.longitude,'E','W'):'');
}
function dms(v,p,n){ const a=Math.abs(v), d=Math.floor(a), m=Math.floor((a-d)*60), s=Math.round(((a-d)*60-m)*60);
  return `${d}°${String(m).padStart(2,'0')}'${String(s).padStart(2,'0')}"${v>=0?p:n}`; }

// Airbus MCDU PERF TAKE OFF page. Colours follow the real box: white labels, cyan pilot
// entries (V-speeds, FLEX, THS, THR RED/ACC), green computed data, amber boxes for
// required-but-empty fields and amber scratchpad messages.
function mcduTakeoff(r,rd){
  const box='<span class="am">□□□</span>', dash='<span class="dim">---</span>';
  const sp=v=>r?`<span class="cy">${v}</span>`:box;
  const L=(l,c,rr)=>`<div class="m-row s"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const D=(l,c,rr)=>`<div class="m-row"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const shift=parseFloat(dep.shift)||0;
  const flex=r?(r.flexTempC!=null?`<span class="cy">${r.flexTempC}°</span>`:'<span class="cy">[  ]°</span>'):dash;
  const thr=r?`<span class="cy">${r.thrRedAltFt}/${r.accAltFt}</span>`:dash;
  const scratch=dep.err||(r?.warnings?.[0]||'');
  return `<div class="fms-screen airbus">
    <div class="m-title">TAKE OFF RWY <span class="gr">${esc(rd.ident)||'---'}</span></div>
    ${L(' V1','FLP RETR','TO SHIFT ')}
    ${D(sp(r?.v1),r?`F=<span class="gr">${r.flapRetractKts}</span>`:'F=---',`<span class="cy">[M][${shift?String(shift).padStart(4,' '):'&nbsp;&nbsp;&nbsp;&nbsp;'}]*</span>`)}
    ${L(' VR','SLT RETR','FLAPS/THS')}
    ${D(sp(r?.vr),r?`S=<span class="gr">${r.slatRetractKts}</span>`:'S=---',r?`<span class="cy">${flapShort(r.flapSetting)}/${r.ths}</span>`:'<span class="am">□/□□□□</span>')}
    ${L(' V2','CLEAN','FLEX TO TEMP')}
    ${D(sp(r?.v2),r?`O=<span class="gr">${r.greenDotKts}</span>`:'O=---',flex)}
    ${L('TRANS ALT','','')}
    ${D(+dep.ta?`<span class="cy">${+dep.ta}</span>`:'<span class="dim">-----</span>','','')}
    ${L('THR RED/ACC','','ENG OUT ACC')}
    ${D(thr,'',r?`<span class="cy">${r.engOutAccAltFt}</span>`:dash)}
    ${L('','','NEXT&nbsp;')}
    ${D('','','PHASE>')}
    <div class="m-scratch ${scratch?'am':''}">${esc(scratch.toUpperCase())}</div>
  </div>
  <div class="fms-cap"><span>MCDU · PERF TAKE OFF</span><span>${r&&r.optimumFlapChosen?'OPT CONF · ':''}${r?esc(r.aircraftName):''}</span></div>`;
}
// Boeing CDU TAKEOFF REF page 1/2 (737/777/787 layout) — white data, green QRH speeds,
// SEL/OAT carries the assumed temperature the way the N1 LIMIT page would.
function cduTakeoffRef(r,rd){
  const L=(l,c,rr)=>`<div class="m-row s"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const D=(l,c,rr)=>`<div class="m-row"><span>${l}</span><span>${c}</span><span>${rr}</span></div>`;
  const q=v=>r?`<span class="gr sm">${v}</span>&nbsp;&nbsp;<span class="wh">${v}&gt;</span>`:'<span class="am">---</span>';
  const agl=v=>r?`${Math.max(0,Math.round((v-rd.elev)/50)*50)}FT`:'----';   // CDU shows AGL, e.g. 1500FT
  const comp=r?`${r.headwindKts>=0?'H':'T'}${String(Math.abs(r.headwindKts)).padStart(2,'0')}/U0.0`:'---/--';
  const shift=parseFloat(dep.shift)||0;
  const scratch=dep.err||(r?.warnings?.[0]||'');
  const oat=dep.oat.trim()===''?'--':dep.oat;
  return `<div class="fms-screen boeing">
    <div class="m-title">TAKEOFF REF<span class="m-pg">1/2</span></div>
    ${L(' FLAPS','','QRH&nbsp;&nbsp;V1')}
    ${D(r?`<span class="wh">${flapShort(r.flapSetting)}°</span>`:'<span class="am">□□</span>','',q(r?.v1))}
    ${L(' E/O ACCEL HT','','QRH&nbsp;&nbsp;VR')}
    ${D(`<span class="wh">${agl(r?.engOutAccAltFt)}</span>`,'',q(r?.vr))}
    ${L(' THR REDUCTION','','QRH&nbsp;&nbsp;V2')}
    ${D(`<span class="wh">${agl(r?.thrRedAltFt)}</span>`,'',q(r?.v2))}
    ${L(' WIND/SLOPE','','TRIM&nbsp;&nbsp;&nbsp;CG')}
    ${D(`<span class="wh">${comp}</span>`,'',r?`<span class="wh">${r.stabTrimUnits.toFixed(2)}&nbsp;&nbsp;${(parseFloat(dep.cg)||28).toFixed(1)}%</span>`:'<span class="dim">----&nbsp;&nbsp;--.-%</span>')}
    ${L(' RWY COND','RW/POS','POS SHIFT')}
    ${D(`<span class="wh">${dep.surface}</span>`,`<span class="wh">${esc(rd.ident)||'---'}</span>`,`<span class="wh">${shift?shift:'----'}M</span>`)}
    ${L(' SEL/OAT','','')}
    ${D(`<span class="wh">${r?(r.flexTempC!=null?r.flexTempC+'°':'---'):'--'}/${esc(oat)}°C</span>`,'','')}
    ${D('<span class="wh sm">&lt;INDEX</span>','','<span class="wh sm">POS INIT&gt;</span>')}
    <div class="m-scratch ${scratch?'am':''}">${esc(scratch.toUpperCase())}</div>
  </div>
  <div class="fms-cap"><span>CDU · TAKEOFF REF</span><span>${r&&r.optimumFlapChosen?'OPT FLAP · ':''}${r?esc(r.aircraftName):''}</span></div>`;
}

// Runway diagram (Flysmart-style, drawn horizontally): the strip to scale with thresholds,
// centreline and both idents; TO shift hatched; the required distance filled from the
// start of the roll and coloured by margin, with the stop margin to the far end.
function runwayDiagram(r,rd){
  const W=600,H=104,x0=34,x1=W-34,len=rd.len||0;
  const shift=Math.min(parseFloat(dep.shift)||0,len);
  const X=m=>x0+(len?m/len:0)*(x1-x0);
  const recip=(()=>{ const m=String(rd.ident||'').match(/^(\d{1,2})([LCR]?)$/); if(!m)return '';
    const n=((+m[1]+18-1)%36)+1; return String(n).padStart(2,'0')+({L:'R',R:'L',C:'C'}[m[2]]||''); })();
  const col=!r?'#8a8f98':r.status==='UNSAFE'?'#ef4444':r.status==='CAUTION'?'#f0a02a':'#3ddc84';
  const reqEnd=r?Math.min(len,shift+r.requiredRunwayM):0;
  const dashes=[]; for(let x=x0+44;x<x1-44;x+=26) dashes.push(`<rect x="${x}" y="50.2" width="14" height="1.6" fill="#e9e9e9" opacity=".85"/>`);
  const keys=end=>{ const out=[]; for(let i=0;i<6;i++){ const y=36+i*5.2; out.push(`<rect x="${end?x1-14:x0+4}" y="${y}" width="10" height="2.6" fill="#f2f2f2"/>`);} return out.join(''); };
  return `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid meet" role="img" aria-label="Runway diagram">
    <defs><pattern id="dpHatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="3" height="6" fill="rgba(255,255,255,.18)"/></pattern></defs>
    <rect x="${x0}" y="32" width="${x1-x0}" height="38" rx="2" fill="#141517" stroke="#5b5e64" stroke-width="1"/>
    ${keys(false)}${keys(true)}${dashes.join('')}
    <text x="${x0-8}" y="51" transform="rotate(-90 ${x0-8} 51)" text-anchor="middle" dominant-baseline="middle" class="rv-id">${esc(rd.ident)||'--'}</text>
    <text x="${x1+8}" y="51" transform="rotate(90 ${x1+8} 51)" text-anchor="middle" dominant-baseline="middle" class="rv-id">${recip}</text>
    ${shift&&len?`<rect x="${x0}" y="32" width="${X(shift)-x0}" height="38" fill="url(#dpHatch)"/><text x="${(x0+X(shift))/2}" y="84" text-anchor="middle" class="rv-sm">TO SHIFT ${shift} m</text>`:''}
    ${r&&len?`<rect x="${X(shift)}" y="32" width="${Math.max(0,X(reqEnd)-X(shift))}" height="38" fill="${col}" opacity=".26"/>
      <line x1="${X(shift)}" y1="26" x2="${X(reqEnd)}" y2="26" stroke="${col}" stroke-width="2"/>
      <path d="M${X(reqEnd)} 26v50" stroke="${col}" stroke-width="2" stroke-dasharray="3 3"/>
      <text x="${(X(shift)+X(reqEnd))/2}" y="20" text-anchor="middle" class="rv-lbl" fill="${col}">REQ ${r.requiredRunwayM.toLocaleString()} m</text>
      ${r.marginM>0?`<text x="${(X(reqEnd)+x1)/2}" y="20" text-anchor="middle" class="rv-sm">STOP MARGIN +${Math.round(r.marginM).toLocaleString()} m</text>`:''}
      <g transform="translate(${X(shift)+14} 51) rotate(90) scale(.5)"><path d="${PLANE}" fill="#fff"/></g>`
      :`<text x="${W/2}" y="20" text-anchor="middle" class="rv-sm">${len?`TORA ${len.toLocaleString()} m`:'Select a runway'}</text>`}
    <text x="${W/2}" y="98" text-anchor="middle" class="rv-sm">${len?`${len.toLocaleString()} m${rd.wid?' × '+rd.wid+' m':''}`:''}</text>
  </svg>`;
}

// ── CHECKLIST APP ──
// An electronic normal checklist in the style of the aircraft being flown (Airbus ECL / Boeing
// ECL / ATR paper C/L), one set per fleet type from EfbChecklistLibrary: menu of checklists on
// the left, the active checklist on the right. Pending lines are white, completed lines green,
// the current line is boxed; tapping a line (or CHECK / Space) completes it. The set follows the
// flight's aircraft type unless the pilot picks one — the choice is shared across devices.
// `activeChecklistId` survives the re-render each WS push causes so the page doesn't jump.
let activeChecklistId=null, activeChecklistSet=null;
const CL_KEYS={
  airbus:{menu:'NORMAL C/L', reset:'C/L RESET', ovrd:'C/L COMPLETE', check:'CHECK', done:'C/L COMPLETE'},
  boeing:{menu:'NORMAL MENU', reset:'CHKL RESET', ovrd:'CHKL OVRD', check:'CHECK ITEM', done:'CHECKLIST COMPLETE'},
  atr:{menu:'NORMAL C/L', reset:'RESET', ovrd:'ALL CHECKED', check:'CHECK', done:'CHECKLIST COMPLETE'},
  generic:{menu:'CHECKLISTS', reset:'RESET', ovrd:'COMPLETE ALL', check:'CHECK', done:'CHECKLIST COMPLETE'},
};
// The ___ blanks the EFB already knows (Departure / Arrival Perf results, the OFP, the squawk).
function clFill(cl,it){
  if(!it.response.includes('___')) return null;
  const c=it.challenge.toUpperCase(), k=cl.phaseKey, d=dep&&dep.result, a=arr&&arr.result;
  const kg=plan.hasPlan&&plan.rampFuelLbs?Math.round(plan.rampFuelLbs/LB_KG/10)*10:0;
  if(/FUEL/.test(c)&&kg&&/KG/.test(it.response)) return [kg.toLocaleString()];
  if(/FUEL/.test(c)&&kg&&/\bLBS?\b/.test(it.response)) return [(Math.round(plan.rampFuelLbs/10)*10).toLocaleString()];
  if(/TAKEOFF SPEEDS/.test(c)&&d) return [d.v1,d.vr,d.v2];
  if(c==='MCP'&&d) return [d.v2];
  if(/FLAP/.test(c)&&k==='before-takeoff'&&d) return [flapShort(d.flapSetting)];
  if(/STABILIZER TRIM|^TRIM$/.test(c)&&d&&d.stabTrimUnits) return [d.stabTrimUnits.toFixed(2)];
  if(/LANDING DATA/.test(c)&&a) return [a.vref];
  if(c==='AUTOBRAKE'&&k==='descent'&&a) return [a.autobrake];
  if(c==='FLAPS'&&k==='landing'&&a) return [flapShort(a.flapSetting)];
  if(/TRANSPONDER/.test(c)&&state.squawk&&k!=='shutdown') return [state.squawk];
  return null;
}
function clResp(cl,it){ const v=clFill(cl,it)||[]; let i=0;
  return esc(it.response).replace(/___/g,()=>{ const x=v[i++];
    return x!=null&&x!==''&&x!==0?`<b class="cl-val" title="From EFB data">${esc(String(x))}</b>`:'<i class="cl-blank"></i>'; }); }
function clSend(path,payload){ return postJSON(path,{setId:clBundle.setId,...payload}).catch(()=>{}); }
// optimistic local tick so the current-line box moves instantly; the WS push confirms it
function clTick(cl,it,on){
  it.isChecked=on; it.checkedAtUtc=on?new Date().toISOString():null;
  cl.checkedCount=cl.items.filter(x=>x.isChecked).length; cl.isComplete=cl.checkedCount===cl.totalCount;
  if(cl.isComplete) cl.completedAtUtc=it.checkedAtUtc;
  renderChecklist();
  clSend('/efb/api/checklists/item',{checklistId:cl.id,itemId:it.id,checked:on});
}
function clClear(cl){ cl.items.forEach(i=>{i.isChecked=false;i.checkedAtUtc=null;}); cl.checkedCount=0; cl.isComplete=false; cl.completedAtUtc=null; }
function renderChecklist(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  const b=clBundle, lists=checklists, K=CL_KEYS[b.style]||CL_KEYS.generic;
  if(activeChecklistSet!==b.setId){ activeChecklistSet=b.setId; activeChecklistId=null; }
  const now=(dbPhaseChecklists(state).find(c=>!c.isComplete)||{}).id;
  if(!lists.some(c=>c.id===activeChecklistId))
    activeChecklistId=now||(lists.find(c=>!c.isComplete)||lists[0]||{}).id;
  const cl=lists.find(c=>c.id===activeChecklistId);
  const idx=lists.indexOf(cl), next=lists[idx+1], prev=idx>0?lists[idx-1]:null;
  const cur=cl?cl.items.find(i=>!i.isChecked):null;
  const doneN=lists.filter(c=>c.isComplete).length;
  const auto=(b.sets||[]).find(s=>s.id===b.autoSetId);
  const tick='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12.5l4.2 4.2L19 7"/></svg>';
  body.innerHTML=`
  <div class="cl cl-${esc(b.style||'generic')}">
    <header class="cl-top">
      <div class="cl-id"><b>NORMAL CHECKLIST</b><span>${esc(b.setName||'')}</span></div>
      <label class="cl-ac" title="Checklist set. AUTO follows the flight's aircraft type.">
        <span>AIRCRAFT</span>
        <select id="clSet">
          <option value="auto" ${b.manual?'':'selected'}>AUTO · ${esc(auto?auto.name:'Generic')}${b.aircraftType?` (${esc(b.aircraftType)})`:''}</option>
          ${(b.sets||[]).map(s=>`<option value="${esc(s.id)}" ${b.manual&&s.id===b.setId?'selected':''}>${esc(s.name)}${s.types.length?' — '+esc(s.types.slice(0,4).join(' ')):''}</option>`).join('')}
        </select>
      </label>
      <div class="cl-top-r">
        <span class="cl-sum mono"><b>${doneN}</b>/${lists.length} COMPLETE</span>
        <button class="cl-btn ghost" id="clResetAll">RESET ALL</button>
      </div>
    </header>
    <div class="cl-grid">
      <nav class="cl-menu" aria-label="Checklists">
        <div class="cl-menu-h">${K.menu}</div>
        ${lists.map(c=>{ const st=c.isComplete?'done':c.checkedCount?'part':'';
          return `<button class="cl-mi ${st} ${c.id===activeChecklistId?'on':''}" data-cl="${esc(c.id)}">
            <i class="cl-dot">${c.isComplete?tick:''}</i>
            <span class="cl-mi-n">${esc(c.name)}${c.id===now?'<em>NOW</em>':''}</span>
            <small class="mono">${c.isComplete?(c.completedAtUtc?fmtT(c.completedAtUtc):'✓'):`${c.checkedCount}/${c.totalCount}`}</small></button>`; }).join('')
          ||'<div class="cl-none">No checklists loaded.</div>'}
      </nav>
      <section class="cl-page">${cl?`
        <div class="cl-page-h"><h2>${esc(cl.name)}</h2><small>${esc(cl.phase)} · <span class="mono">${cl.checkedCount}/${cl.totalCount}</span></small></div>
        <div class="cl-lines" id="clLines">${cl.items.map(it=>`
          ${it.lineBefore?'<div class="cl-sep" role="separator"></div>':''}
          <button class="cl-ln ${it.isChecked?'done':''} ${cur&&it.id===cur.id?'cur':''}" data-item="${esc(it.id)}" aria-pressed="${it.isChecked}">
            <i class="cl-ck">${it.isChecked?tick:''}</i>
            <span class="cl-ch">${esc(it.challenge)}</span>
            <span class="cl-lead"></span>
            <span class="cl-rsp">${clResp(cl,it)}${it.who?` <em class="cl-who">(${esc(it.who)})</em>`:''}</span>
            ${it.note?`<small class="cl-note">${esc(it.note)}</small>`:''}
          </button>`).join('')}
        </div>
        ${cl.isComplete?`<div class="cl-complete"><span>${K.done}</span>${cl.completedAtUtc?`<small class="mono">${fmtT(cl.completedAtUtc)}</small>`:''}
          ${next?`<button class="cl-btn" data-go="${esc(next.id)}">NEXT · ${esc(next.name)} ›</button>`:''}</div>`:''}
        <footer class="cl-keys">
          <button class="cl-btn ghost" id="clPrev" ${prev?'':'disabled'}>‹ PREV</button>
          <button class="cl-btn ghost" id="clReset">${K.reset}</button>
          <button class="cl-btn ghost" id="clOvrd" ${cl.isComplete?'disabled':''}>${K.ovrd}</button>
          <button class="cl-btn go" id="clCheck" ${cur?'':'disabled'}>${K.check}</button>
          <button class="cl-btn ghost" id="clNext" ${next?'':'disabled'}>NEXT ›</button>
        </footer>`
        :'<div class="cl-none">Select a checklist.</div>'}
      </section>
    </div>
    <p class="cl-src">${esc(b.source||'')} <b>For simulation use only.</b></p>
  </div>`;
  const go=id=>{ activeChecklistId=id; renderChecklist(); };
  $$('#appBody .cl-mi').forEach(x=>x.onclick=()=>go(x.dataset.cl));
  $$('#appBody [data-go]').forEach(x=>x.onclick=()=>go(x.dataset.go));
  $('#clSet').onchange=async e=>{ try{ setChecklists(await postJSON('/efb/api/checklists/select',{setId:e.target.value})); }catch{} renderChecklist(); };
  $('#clResetAll').onclick=()=>{ lists.forEach(clClear); activeChecklistId=null; renderChecklist(); clSend('/efb/api/checklists/reset',{}); };
  // on a phone the menu is a sideways strip: bring the open checklist into view
  const on=$('#appBody .cl-mi.on'), menu=on&&on.parentElement;
  if(on&&menu.scrollWidth>menu.clientWidth) menu.scrollLeft=on.offsetLeft-menu.offsetLeft-12;
  if(!cl) return;
  $$('#clLines .cl-ln').forEach(row=>row.onclick=()=>{ const it=cl.items.find(i=>i.id===row.dataset.item); if(it) clTick(cl,it,!it.isChecked); });
  $('#clCheck').onclick=()=>{ if(cur) clTick(cl,cur,true); };
  $('#clPrev').onclick=()=>prev&&go(prev.id);
  $('#clNext').onclick=()=>next&&go(next.id);
  $('#clReset').onclick=()=>{ clClear(cl); renderChecklist(); clSend('/efb/api/checklists/reset',{checklistId:cl.id}); };
  $('#clOvrd').onclick=()=>{ const t=new Date().toISOString(); cl.items.forEach(i=>{ if(!i.isChecked){i.isChecked=true;i.checkedAtUtc=t;} });
    cl.checkedCount=cl.totalCount; cl.isComplete=true; cl.completedAtUtc=t; renderChecklist();
    clSend('/efb/api/checklists/complete',{checklistId:cl.id}); };
  const c=$('#clLines .cur'); if(c) c.scrollIntoView({block:'nearest'});
}
// Space / Enter completes the current line, Backspace un-ticks the last one (keyboard / BT remote).
document.addEventListener('keydown',e=>{
  if(activeApp!=='checklist'||e.defaultPrevented||/INPUT|SELECT|TEXTAREA|BUTTON/.test(document.activeElement?.tagName||''))return;
  const cl=checklists.find(c=>c.id===activeChecklistId); if(!cl)return;
  if(e.key===' '||e.key==='Enter'){ const cur=cl.items.find(i=>!i.isChecked); if(cur){ e.preventDefault(); clTick(cl,cur,true); } }
  else if(e.key==='Backspace'){ const last=[...cl.items].reverse().find(i=>i.isChecked); if(last){ e.preventDefault(); clTick(cl,last,false); } }
});
function fmtT(iso){ try{return new Date(iso).toISOString().substr(11,5)+'Z';}catch{return '';} }

// ── MANUALS APP ──
// The AviatesAir document library, laid out like an airline's EFB document reader (Comply365 /
// Web Manuals / the Fenix EFB's docs): a library tree on the left (MUST READ notices, the
// manuals for your aircraft, bookmarks, then CN · OM-A · OM-B · QRH · GUIDE), the reader on
// the right with chapter header, revision/effective block, "on this page" TOC with scroll-spy,
// text size, bookmarks, READ & ACKNOWLEDGE for Company NOTAMs, cross-document links and
// full-text search with section hits. Export PDF opens the print book (?print=…), which the
// Discord PDFs are made from too.
let man={idx:null, docs:{}, open:null, anchor:null, q:'', hits:null, hl:'', collapsed:{}, pdfMenu:false};
const MN_SIZE_KEY='efbDocSize';
let mnSize=(()=>{ try{ return +localStorage.getItem(MN_SIZE_KEY)||1; }catch{ return 1; } })();
const MN_MON=['JAN','FEB','MAR','APR','MAY','JUN','JUL','AUG','SEP','OCT','NOV','DEC'];
const mnDate=s=>{ const m=String(s||'').match(/^(\d{4})-(\d{2})-(\d{2})/); return m?`${m[3]} ${MN_MON[+m[2]-1]} ${m[1]}`:(s||''); };
const mnChap=d=>{ const c=String(d.chapter||''); return c.toUpperCase().startsWith(d.manual)?c:`${d.manual} ${c}`; };
const mnAnchor=t=>String(t).toLowerCase().replace(/[^\p{L}\p{N}]/gu,'-').replace(/-+/g,'-').replace(/^-|-$/g,'')||'section';
const mnStrip=t=>String(t).replace(/\[([^\]]+)\]\([^)]+\)/g,'$1').replace(/[*`]/g,'');
const mnManual=code=>(man.idx?.manuals||[]).find(m=>m.code===code)||{code,name:code,description:''};
const mnMark=(html,q)=>{ if(!q) return html; const re=new RegExp(q.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'),'gi'); return html.replace(re,m=>`<mark>${m}</mark>`); };

// Markdown → HTML for the manuals dialect (see Core/Efb/EfbManuals.cs). `prefix` namespaces
// heading ids; `link(id,anchor)` builds a cross-document href (the print book links in-document).
function mdx(src,{prefix='h-',link=null}={}){
  const e=t=>t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
  const inl=t=>e(t)
    .replace(/\[([^\]]+)\]\(doc:([^)#\s]+)(?:#([^)\s]+))?\)/g,(m,tx,id,a)=>
      `<a class="mn-link" href="${link?link(id,a||''):'#'}" data-doc="${id}" data-anchor="${a||''}">${tx}</a>`)
    .replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g,'<a href="$2" target="_blank" rel="noopener">$1</a>')
    .replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>')
    .replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?![*\w])/g,'$1<em>$2</em>')
    .replace(/`([^`]+)`/g,'<code>$1</code>');
  const L=String(src||'').replace(/\r/g,'').split('\n'); let h='', i=0;
  const cells=r=>r.trim().replace(/^\||\|$/g,'').split('|').map(c=>c.trim());
  while(i<L.length){ const l=L[i];
    if(/^\|.*\|\s*$/.test(l)&&/^\|[\s:|-]+\|\s*$/.test(L[i+1]||'')){
      const hd=cells(l); i+=2; const rows=[];
      while(i<L.length&&/^\|/.test(L[i])){ rows.push(cells(L[i])); i++; }
      h+=`<div class="mn-tw"><table><thead><tr>${hd.map(x=>`<th>${inl(x)}</th>`).join('')}</tr></thead><tbody>${rows.map(r=>
        r.every(c=>/^─+$/.test(c))?`<tr class="mn-cline"><td colspan="${hd.length}"></td></tr>`
          :`<tr>${r.map(c=>`<td>${inl(c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
      continue; }
    let m;
    if((m=l.match(/^(#{2,3}) (.+)/))){ const lv=m[1].length, a=mnAnchor(mnStrip(m[2]));
      h+=`<h${lv} id="${prefix}${a}">${inl(m[2])}</h${lv}>`; i++; continue; }
    if(/^---\s*$/.test(l)){ h+='<hr>'; i++; continue; }
    if(/^> /.test(l)){ const q=[]; while(i<L.length&&/^> ?/.test(L[i])){ q.push(L[i].replace(/^> ?/,'')); i++; }
      const t=q.join(' '), c=t.match(/^\*\*(NOTE|CAUTION|WARNING)\*\*\s*/);
      h+=c?`<div class="mn-call ${c[1].toLowerCase()}"><b>${c[1]}</b><p>${inl(t.slice(c[0].length))}</p></div>`:`<blockquote>${inl(t)}</blockquote>`;
      continue; }
    if(/^\d+\. /.test(l)){ const it=[]; while(i<L.length&&/^\d+\. /.test(L[i])){ it.push(L[i].replace(/^\d+\. /,'')); i++; }
      h+=`<ol>${it.map(x=>`<li>${inl(x)}</li>`).join('')}</ol>`; continue; }
    if(/^- /.test(l)){ const it=[]; while(i<L.length&&/^- /.test(L[i])){ it.push(L[i].slice(2)); i++; }
      h+=`<ul>${it.map(x=>`<li>${inl(x)}</li>`).join('')}</ul>`; continue; }
    if(l.trim()) h+=`<p>${inl(l)}</p>`;
    i++; }
  return h;
}

async function mnLoadIndex(){ man.idx=await getJSON('/efb/api/manuals'); renderBadges(); }
async function mnDoc(id){
  if(!man.docs[id]) man.docs[id]=await getJSON('/efb/api/manuals/'+encodeURIComponent(id));
  return man.docs[id];
}
function mnUpdateStatus(st){ if(!st||!man.idx) return;
  const i=man.idx.documents.findIndex(d=>d.id===st.id); if(i>=0) man.idx.documents[i]=st;
  if(man.docs[st.id]) man.docs[st.id].status=st; }

async function renderManuals(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  if(!man.idx){
    body.innerHTML=`<div class="mn"><div class="mn-none">Loading the document library…</div></div>`;
    try{ await mnLoadIndex(); }catch{ body.innerHTML=`<div class="mn"><div class="mn-none">Could not load the document library.</div></div>`; return; }
    if(activeApp!=='manuals') return;
  }
  body.innerHTML=`
  <div class="mn">
    <header class="mn-top">
      <button class="mn-id" id="mnHome" title="Library home"><b>DOCUMENT LIBRARY</b><span>AviatesAir Operations Manuals</span></button>
      <label class="mn-search"><svg ${SVG_ATTRS}><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/></svg>
        <input id="mnQ" type="search" placeholder="Search all manuals, e.g. final reserve, crosswind, FLAPS 5" value="${esc(man.q)}" autocomplete="off" spellcheck="false"></label>
      <div class="mn-pdfw">
        <button class="mn-btn" id="mnPdf" aria-haspopup="true">EXPORT PDF</button>
        <div class="mn-pdfm ${man.pdfMenu?'':'hidden'}" id="mnPdfM"></div>
      </div>
    </header>
    <div class="mn-grid">
      <nav class="mn-nav" id="mnNav" aria-label="Library"></nav>
      <section class="mn-view" id="mnView"></section>
    </div>
  </div>`;
  let t=null;
  $('#mnQ').oninput=e=>{ clearTimeout(t); const v=e.target.value; t=setTimeout(()=>mnSearch(v),220); };
  $('#mnQ').onkeydown=e=>{ if(e.key==='Escape'){ e.preventDefault(); e.target.value=''; mnSearch(''); } };
  $('#mnHome').onclick=()=>{ man.open=null; man.q=''; man.hits=null; $('#mnQ').value=''; mnPaint(); };
  $('#mnPdf').onclick=e=>{ e.stopPropagation(); man.pdfMenu=!man.pdfMenu; mnPdfMenu(); };
  const close=()=>{ if(man.pdfMenu){ man.pdfMenu=false; mnPdfMenu(); } };
  document.addEventListener('click',close);
  const sc=$('#appBody'), spy=()=>mnSpy();
  sc.addEventListener('scroll',spy,{passive:true});
  appCleanup=()=>{ document.removeEventListener('click',close); sc.removeEventListener('scroll',spy); };
  mnPaint();
}
function mnPaint(){ mnNavPaint(); mnViewPaint(); mnPdfMenu(); }

function mnPdfMenu(){
  const m=$('#mnPdfM'); if(!m) return;
  m.classList.toggle('hidden',!man.pdfMenu);
  const cur=man.open&&man.idx.documents.find(d=>d.id===man.open);
  const opts=[...(cur?[[cur.id,`This document: ${mnChap(cur)}`],[cur.manual,`Whole manual: ${mnManual(cur.manual).name}`]]:[]),
    ...man.idx.manuals.filter(x=>!cur||x.code!==cur.manual).map(x=>[x.code,`${x.code}: ${x.name}`]),['ALL','Complete library (all manuals)']];
  m.innerHTML=`<div class="mn-pdfh">Opens a print-ready copy. Choose <b>Save as PDF</b> in the print dialog.</div>`
    +opts.map(([k,l])=>`<button data-pdf="${esc(k)}">${esc(l)}</button>`).join('');
  $$('#mnPdfM [data-pdf]').forEach(b=>b.onclick=e=>{ e.stopPropagation(); man.pdfMenu=false; mnPdfMenu();
    window.open(`${location.pathname}?print=${encodeURIComponent(b.dataset.pdf)}`,'_blank'); });
}

function mnNavPaint(){
  const nav=$('#mnNav'); if(!nav) return;
  const d=man.idx, docs=d.documents;
  const it=x=>`<button class="mn-it ${x.id===man.open?'on':''}" data-doc="${esc(x.id)}">
      <em>${esc(mnChap(x))}</em><span>${esc(x.title)}</span>
      ${x.requiresAck?(x.acknowledgedUtc?'<i class="ok" title="Acknowledged">✓</i>':'<i class="due" title="Awaiting acknowledgement">!</i>'):x.bookmarked?'<i class="bm">★</i>':''}</button>`;
  const grp=(key,title,list,cls='')=>{ const col=man.collapsed[key];
    return `<div class="mn-g ${cls}"><button class="mn-gh" data-grp="${key}" aria-expanded="${!col}"><span>${title}</span><small>${list.length}</small><svg ${SVG_ATTRS}><path d="${col?'M9 6l6 6-6 6':'M6 9l6 6 6-6'}"/></svg></button>
      ${col?'':`<div class="mn-gl">${list.map(it).join('')}</div>`}</div>`; };
  const pend=docs.filter(x=>x.requiresAck&&!x.acknowledgedUtc);
  const mine=d.aircraftSetId&&d.aircraftSetId!=='generic'?docs.filter(x=>x.aircraft.includes(d.aircraftSetId)):[];
  const bms=docs.filter(x=>x.bookmarked);
  nav.innerHTML=(pend.length?grp('must','Must Read',pend,'must'):'')
    +(mine.length?grp('mine',`Your Aircraft · ${esc(d.aircraftType||'')}`,mine,'mine'):'')
    +(bms.length?grp('bm','Bookmarks',bms):'')
    +d.manuals.map(m=>grp('m-'+m.code,`<b>${esc(m.code)}</b> ${esc(m.name)}`,docs.filter(x=>x.manual===m.code))).join('');
  $$('#mnNav [data-doc]').forEach(b=>b.onclick=()=>mnOpen(b.dataset.doc));
  $$('#mnNav [data-grp]').forEach(b=>b.onclick=()=>{ man.collapsed[b.dataset.grp]=!man.collapsed[b.dataset.grp]; mnNavPaint(); });
}

async function mnSearch(q){
  man.q=q.trim();
  if(man.q.length<2){ man.hits=null; mnViewPaint(); return; }
  const mine=man.q;
  try{ const hits=await getJSON('/efb/api/manuals/search?q='+encodeURIComponent(mine)); if(man.q!==mine) return; man.hits=hits; }
  catch{ man.hits=[]; }
  mnViewPaint();
}

async function mnOpen(id,anchor=''){
  man.open=id; man.anchor=anchor||null; man.hl=man.q.length>=2?man.q:'';
  man.q=''; man.hits=null; const qi=$('#mnQ'); if(qi) qi.value='';
  mnNavPaint();
  const v=$('#mnView'); if(v&&!man.docs[id]) v.innerHTML=`<div class="mn-none">Opening…</div>`;
  try{ await mnDoc(id); }catch{ if(v) v.innerHTML=`<div class="mn-none">Could not open this document.</div>`; return; }
  if(activeApp!=='manuals'||man.open!==id) return;
  mnViewPaint();
}

function mnViewPaint(){
  const v=$('#mnView'); if(!v) return;
  if(man.hits) return mnResults(v);
  if(man.open&&man.docs[man.open]) return mnReader(v,man.docs[man.open]);
  mnHomeView(v);
}

function mnResults(v){
  const q=man.q, hits=man.hits;
  v.innerHTML=`<div class="mn-res">
    <div class="mn-res-h">${hits.length?`${hits.length}${hits.length>=40?'+':''} result${hits.length===1?'':'s'} for <b>“${esc(q)}”</b>`:`No results for <b>“${esc(q)}”</b>`}</div>
    ${hits.map(h=>`<button class="mn-hit" data-doc="${esc(h.docId)}" data-anchor="${esc(h.anchor)}">
      <span class="mn-hit-t"><span class="mn-chip">${esc(h.manual)}</span><b>${esc(h.title)}</b>${h.section?`<em>› ${esc(h.section)}</em>`:''}</span>
      <p>${mnMark(esc(h.snippet),q)}</p></button>`).join('')}
  </div>`;
  $$('#mnView [data-doc]').forEach(b=>b.onclick=()=>mnOpen(b.dataset.doc,b.dataset.anchor));
}

function mnHomeView(v){
  const d=man.idx, docs=d.documents;
  const pend=docs.filter(x=>x.requiresAck&&!x.acknowledgedUtc);
  const mine=d.aircraftSetId&&d.aircraftSetId!=='generic'?docs.filter(x=>x.aircraft.includes(d.aircraftSetId)):[];
  const recent=(d.recent||[]).map(id=>docs.find(x=>x.id===id)).filter(Boolean).slice(0,5);
  const latest=docs.map(x=>x.effective).sort().slice(-1)[0];
  const card=x=>`<button class="mn-dc" data-doc="${esc(x.id)}"><em>${esc(mnChap(x))}</em><b>${esc(x.title)}</b><span>${esc(x.summary)}</span></button>`;
  v.innerHTML=`<div class="mn-home">
    <div class="mn-hero">
      <div><b>Operations library</b><span>${docs.length} documents. Latest revision ${esc(mnDate(latest))}.</span></div>
    </div>
    ${pend.length?`<section class="mn-must"><header><b>MUST READ</b><span>${pend.length} Company NOTAM${pend.length>1?'s':''} awaiting your acknowledgement</span></header>
      <div class="mn-dcs">${pend.map(card).join('')}</div></section>`
      :`<section class="mn-must ok"><header><b>ALL NOTICES ACKNOWLEDGED</b><span>You're up to date with every active Company NOTAM.</span></header></section>`}
    ${mine.length?`<section class="mn-sec"><h3>For your ${esc(d.aircraftType)}</h3><div class="mn-dcs">${mine.map(card).join('')}</div></section>`:''}
    <section class="mn-sec"><h3>Manuals</h3><div class="mn-mans">${d.manuals.map(m=>{ const n=docs.filter(x=>x.manual===m.code);
      return `<button class="mn-man" data-man="${esc(m.code)}"><span class="mn-man-c">${esc(m.code)}</span><span class="mn-man-t"><b>${esc(m.name)}</b><span>${esc(m.description)}</span></span><small class="mono">${n.length} doc${n.length===1?'':'s'}</small></button>`; }).join('')}</div></section>
    ${recent.length?`<section class="mn-sec"><h3>Recently opened</h3><div class="mn-dcs">${recent.map(card).join('')}</div></section>`:''}
    <p class="mn-foot">Controlled in the EFB. Printed copies and PDFs are uncontrolled: check the revision before use. For flight simulation only; not for real-world aviation.</p>
  </div>`;
  $$('#mnView [data-doc]').forEach(b=>b.onclick=()=>mnOpen(b.dataset.doc));
  $$('#mnView [data-man]').forEach(b=>b.onclick=()=>{ const f=docs.find(x=>x.manual===b.dataset.man); if(f) mnOpen(f.id); });
}

function mnReader(v,{document:doc,status:st}){
  const docs=man.idx.documents, same=docs.filter(x=>x.manual===doc.manual), i=same.findIndex(x=>x.id===doc.id);
  const prev=same[i-1], next=same[i+1], M=mnManual(doc.manual);
  const ack=doc.requiresAck, acked=st&&st.acknowledgedUtc;
  const toc=(doc.sections||[]).filter(s=>s.level<=3);
  v.innerHTML=`<article class="mn-doc" style="--fs:${mnSize}">
    <header class="mn-dh">
      <div class="mn-dh-top">
        <span class="mn-chip">${esc(doc.manual)}</span><span class="mn-chap mono">${esc(mnChap(doc))}</span>
        ${ack?`<span class="mn-ackc ${acked?'ok':'due'}">${acked?`ACKNOWLEDGED ${esc(mnDate(acked.slice(0,10)))} ${esc(acked.slice(11,16))}Z`:'READ & ACKNOWLEDGE'}</span>`:''}
        <span class="mn-tools">
          <button data-act="bm" class="${st&&st.bookmarked?'on':''}" title="${st&&st.bookmarked?'Remove bookmark':'Bookmark'}" aria-pressed="${!!(st&&st.bookmarked)}">★</button>
          <button data-act="smaller" title="Smaller text">A−</button><button data-act="larger" title="Larger text">A+</button>
          <button data-act="pdf" title="Export this document as PDF">PDF</button>
        </span>
      </div>
      <h1>${esc(doc.title)}</h1>
      <p>${esc(doc.summary)}</p>
      <div class="mn-meta mono"><span>${esc(M.name)}</span><span>REV ${doc.revision}</span><span>EFFECTIVE ${esc(mnDate(doc.effective))}</span></div>
    </header>
    <div class="mn-db">
      <div class="mn-md" id="mnMd">${mdx(doc.bodyMarkdown)}</div>
      ${toc.length>1?`<aside class="mn-toc"><b>ON THIS PAGE</b>${toc.map(s=>`<a href="#" data-to="${esc(s.anchor)}" class="l${s.level}">${esc(s.title)}</a>`).join('')}</aside>`:''}
    </div>
    ${ack?`<div class="mn-ackb ${acked?'ok':''}">${acked
      ?`<b>Acknowledged</b><span>You confirmed you have read and understood ${esc(mnChap(doc))} (revision ${doc.revision}) on ${esc(mnDate(acked.slice(0,10)))} at ${esc(acked.slice(11,16))}Z.</span>`
      :`<span>By acknowledging, you confirm you have read and understood <b>${esc(mnChap(doc))}</b> and will apply it from your next flight.</span><button class="mn-btn go" id="mnAck">ACKNOWLEDGE</button>`}</div>`:''}
    <nav class="mn-pn">${prev?`<button data-doc="${esc(prev.id)}"><small>‹ PREVIOUS</small><span>${esc(mnChap(prev))} · ${esc(prev.title)}</span></button>`:'<span></span>'}
      ${next?`<button data-doc="${esc(next.id)}" class="nx"><small>NEXT ›</small><span>${esc(mnChap(next))} · ${esc(next.title)}</span></button>`:'<span></span>'}</nav>
  </article>`;
  const md=$('#mnMd');
  if(man.hl) mnHighlight(md,man.hl);
  $$('#mnView .mn-link').forEach(a=>a.onclick=e=>{ e.preventDefault(); if(a.dataset.doc===doc.id) mnGoto(a.dataset.anchor); else mnOpen(a.dataset.doc,a.dataset.anchor); });
  $$('#mnView .mn-pn [data-doc]').forEach(b=>b.onclick=()=>mnOpen(b.dataset.doc));
  $$('#mnView [data-to]').forEach(a=>a.onclick=e=>{ e.preventDefault(); mnGoto(a.dataset.to); });
  $$('#mnView [data-act]').forEach(b=>b.onclick=async()=>{
    const act=b.dataset.act;
    if(act==='smaller'||act==='larger'){ mnSize=clamp(Math.round((mnSize+(act==='larger'?.1:-.1))*10)/10,.8,1.5);
      try{ localStorage.setItem(MN_SIZE_KEY,mnSize); }catch{} $('.mn-doc').style.setProperty('--fs',mnSize); return; }
    if(act==='pdf'){ window.open(`${location.pathname}?print=${encodeURIComponent(doc.id)}`,'_blank'); return; }
    if(act==='bm'){ try{ mnUpdateStatus(await postJSON(`/efb/api/manuals/${encodeURIComponent(doc.id)}/bookmark`,{})); }catch{} mnPaint(); }
  });
  const ab=$('#mnAck');
  if(ab) ab.onclick=async()=>{ ab.disabled=true;
    try{ const r=await postJSON(`/efb/api/manuals/${encodeURIComponent(doc.id)}/ack`,{}); mnUpdateStatus(r.status); man.idx.pending=r.pending; renderBadges(); }
    catch{ ab.disabled=false; return; }
    mnPaint(); };
  if(man.anchor){ const a=man.anchor; man.anchor=null; setTimeout(()=>mnGoto(a),30); }   // not rAF: paused in background tabs
  else $('#appBody').scrollTop=0;
  mnSpy();
}
function mnGoto(a){ const el=document.getElementById('h-'+a); if(!el) return;
  const sc=$('#appBody'), y=(el.getBoundingClientRect().top-sc.getBoundingClientRect().top)/uiScaleOf(sc)+sc.scrollTop-12, y0=sc.scrollTop;
  sc.scrollTo({top:y,behavior:'smooth'});
  setTimeout(()=>{ if(sc.scrollTop===y0&&Math.abs(y-y0)>4) sc.scrollTop=y; mnSpy(); },450);   // smooth scroll never started (reduced motion / hidden tab)
  el.classList.remove('mn-flash'); void el.offsetWidth; el.classList.add('mn-flash'); }
function mnSpy(){
  const links=$$('#mnView [data-to]'); if(!links.length) return;
  const top=$('#appBody').getBoundingClientRect().top+140; let cur=links[0].dataset.to;
  for(const a of links){ const el=document.getElementById('h-'+a.dataset.to); if(el&&el.getBoundingClientRect().top<=top) cur=a.dataset.to; }
  links.forEach(a=>a.classList.toggle('on',a.dataset.to===cur));
}
function mnHighlight(root,q){
  const re=new RegExp(q.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'),'gi');
  const w=document.createTreeWalker(root,NodeFilter.SHOW_TEXT); const nodes=[]; while(w.nextNode()) nodes.push(w.currentNode);
  let first=null;
  nodes.forEach(n=>{ if(!re.test(n.nodeValue)) return; re.lastIndex=0;
    const f=document.createDocumentFragment(); let last=0, m;
    while((m=re.exec(n.nodeValue))){ f.append(n.nodeValue.slice(last,m.index)); const mk=document.createElement('mark'); mk.textContent=m[0]; f.append(mk); first=first||mk; last=m.index+m[0].length; }
    f.append(n.nodeValue.slice(last)); n.replaceWith(f); });
  man.hl='';
  return first;
}

// ── PRINT BOOK (?print=CN | OM-A | … | ALL | <doc id>) ──
// The PDF edition: A4 cover in the brand colours, list of effective documents, contents, then
// every document on its own page with in-book cross-reference links. Opened from Manuals →
// Export PDF (the print dialog's "Save as PDF"); `&auto=0` skips the dialog (headless builds).
async function printBook(target){
  document.body.classList.add('printing');
  $('#pairing').classList.add('hidden'); $('#app').classList.add('hidden');
  const root=document.createElement('main'); root.className='pr'; root.innerHTML='<p class="pr-wait">Preparing the document…</p>';
  document.body.appendChild(root);
  let idx; try{ idx=await getJSON('/efb/api/manuals'); }catch{ root.innerHTML='<p class="pr-wait">Could not reach the EFB host. Open Export PDF from a paired device.</p>'; return; }
  man.idx=idx;
  const all=idx.documents;
  let list=target==='ALL'?all:all.filter(d=>d.manual===target);
  const single=!list.length?all.filter(d=>d.id===target):null;
  if(single) list=single;
  if(!list.length){ root.innerHTML='<p class="pr-wait">Nothing to print.</p>'; return; }
  const full=await Promise.all(list.map(d=>getJSON('/efb/api/manuals/'+encodeURIComponent(d.id)).then(r=>r.document)));
  const inBook=new Set(full.map(d=>d.id));
  const link=(id,a)=>inBook.has(id)?`#d-${id}-${a||'top'}`:'#';
  const M=single?mnManual(list[0].manual):target==='ALL'?{code:'LIBRARY',name:'Operations Manual Library',description:'Company NOTAMs, Operations Manual Parts A and B, Quick Reference Handbook and Pilot Guides'}:mnManual(target);
  const title=single?full[0].title:M.name, code=single?mnChap(list[0]):M.code;
  const eff=full.map(d=>d.effective).sort().slice(-1)[0];
  document.title=`AviatesAir ${code}: ${title}`;
  const st=document.createElement('style');
  st.textContent=`@page{@bottom-left{content:"AviatesAir · ${code.replace(/"/g,'')} · Rev ${Math.max(...full.map(d=>d.revision))} · ${mnDate(eff)} · Uncontrolled when printed"}}`;
  document.head.appendChild(st);
  root.innerHTML=`
    <section class="pr-cover">
      <img class="pr-logo" src="logo-white.webp" alt="AviatesAir">
      <div class="pr-cover-m">
        <span class="pr-code">${esc(code)}</span>
        <h1>${esc(title)}</h1>
        <p>${esc(single?full[0].summary:M.description)}</p>
      </div>
      <dl class="pr-cover-f">
        <div><dt>Revision</dt><dd>${Math.max(...full.map(d=>d.revision))}</dd></div>
        <div><dt>Effective</dt><dd>${esc(mnDate(eff))}</dd></div>
        <div><dt>Documents</dt><dd>${full.length}</dd></div>
        <div><dt>Issued by</dt><dd>AviatesAir Flight Operations</dd></div>
      </dl>
      <p class="pr-cover-n">This copy is uncontrolled once printed or saved. The current revision is always in the AviatesAir EFB (Manuals).<br>For flight simulation use only. Not for real-world aviation.</p>
    </section>
    ${full.length>1?`<section class="pr-front">
      <h2>List of effective documents</h2>
      <table class="pr-led"><thead><tr><th>Document</th><th>Title</th><th>Rev</th><th>Effective</th></tr></thead>
        <tbody>${full.map(d=>`<tr><td>${esc(mnChap(d))}</td><td><a href="#d-${d.id}-top">${esc(d.title)}</a></td><td>${d.revision}</td><td>${esc(mnDate(d.effective))}</td></tr>`).join('')}</tbody></table>
      <h2>Contents</h2>
      <ol class="pr-toc">${full.map(d=>`<li><a href="#d-${d.id}-top"><b>${esc(mnChap(d))}</b> ${esc(d.title)}</a>
        <ul>${(d.sections||[]).filter(s=>s.level===2).map(s=>`<li><a href="#d-${d.id}-${s.anchor}">${esc(s.title)}</a></li>`).join('')}</ul></li>`).join('')}</ol>
    </section>`:''}
    ${full.map(d=>`<section class="pr-doc" id="d-${d.id}-top">
      <header class="pr-dh"><span class="pr-chip">${esc(d.manual)}</span><span class="pr-chap">${esc(mnChap(d))}</span>
        <span class="pr-rev">Rev ${d.revision} · Effective ${esc(mnDate(d.effective))}${d.requiresAck?' · READ & ACKNOWLEDGE':''}</span></header>
      <h1>${esc(d.title)}</h1><p class="pr-sum">${esc(d.summary)}</p>
      <div class="mn-md">${mdx(d.bodyMarkdown,{prefix:`d-${d.id}-`,link})}</div>
    </section>`).join('')}`;
  await Promise.all([...root.querySelectorAll('img')].map(im=>im.complete?1:new Promise(r=>{ im.onload=im.onerror=r; })));
  if(new URLSearchParams(location.search).get('auto')!=='0') setTimeout(()=>window.print(),350);
}

// ── MAP APP ──
// A moving map in the Navigraph Charts / flyPad / VATSIM Radar idiom: full-screen map, a
// vertical tool stack (layers, follow ownship, fit route, zoom), a layers sheet with one
// switch per overlay, tap-for-details cards, and an ownship data strip.
//   Base map  — Esri dark / light gray canvas or World Imagery (keyless)
//   Weather   — RainViewer radar (animated loop) + IR satellite; SIGMETs/AIRMETs and METAR
//               flight-category dots via the host's aviationweather.gov proxy
//   Traffic   — AviatesAir pilots, and VATSIM / IVAO traffic and ATC as separate toggles.
//               Aircraft draw on one canvas (thousands of targets stay smooth); ATC shows as
//               staffed FIR/TRACON sectors plus airport D/G/T/A/ATIS badges, resolved on the
//               host from VATSpy/SimAware data
//   Overlays  — flight-plan route with waypoint labels, range rings, callsign labels
// Layer choices persist per device (localStorage) — a convenience, never shared state.
let leafPromise=null;
function loadLeaflet(){
  if(window.L)return Promise.resolve();
  if(leafPromise)return leafPromise;
  leafPromise=new Promise((res,rej)=>{
    const css=document.createElement('link');css.rel='stylesheet';css.href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';document.head.appendChild(css);
    const s=document.createElement('script');s.src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';s.onload=res;s.onerror=()=>{leafPromise=null;rej();};document.head.appendChild(s);
  });
  return leafPromise;
}
const MAP_PREF_KEY='efbMapPrefs';
const MAP_DEFAULTS={base:'dark', route:true, rings:false, labels:true, follow:true, company:true,
  vatsimP:false, vatsimA:false, ivaoP:false, ivaoA:false, radar:false, sat:false, sigmet:false, metar:false};
function mapPrefs(){ try{ return {...MAP_DEFAULTS,...JSON.parse(localStorage.getItem(MAP_PREF_KEY)||'{}')}; }catch{ return {...MAP_DEFAULTS}; } }
function saveMapPrefs(p){ try{ localStorage.setItem(MAP_PREF_KEY,JSON.stringify(p)); }catch{} }
const esriUrl=p=>`https://server.arcgisonline.com/ArcGIS/rest/services/${p}/MapServer/tile/{z}/{y}/{x}`;
const BASEMAPS={
  dark: {name:'Dark',      base:'Canvas/World_Dark_Gray_Base',  ref:'Canvas/World_Dark_Gray_Reference'},
  light:{name:'Light',     base:'Canvas/World_Light_Gray_Base', ref:'Canvas/World_Light_Gray_Reference'},
  sat:  {name:'Satellite', base:'World_Imagery',                ref:'Reference/World_Boundaries_and_Places'},
};
const NET_COL={VATSIM:'#F5B53D', IVAO:'#7C9CFF'}, COMPANY_COL='#3DDC84', OWN_COL='#E63FA6';
const FAC_COL={DEL:'#5B8CFF', GND:'#3DDC84', TWR:'#FF5A5A', APP:'#FFB547', ATIS:'#E7E9EC'};
const CAT_COL={VFR:'#3DDC84', MVFR:'#4B9DFF', IFR:'#FF5757', LIFR:'#D85CF0'};
const hazCol=h=>/TS|CONV/.test(h)?'#FF5A5A':/TURB|TB/.test(h)?'#FFB547':/ICE|IC/.test(h)?'#5BD1FF':/VA/.test(h)?'#D85CF0':/MTW/.test(h)?'#C9A56B':'#E7E9EC';
const MAP_LAYERS=[
  {sec:'Weather', items:[['radar','Weather radar','RainViewer · 10-min loop'],['sat','Satellite IR','RainViewer'],
    ['sigmet','SIGMETs / AIRMETs','aviationweather.gov'],['metar','METAR flight categories','Zoom in to see stations']]},
  {sec:'Traffic', items:[['company','AviatesAir pilots',''],['vatsimP','VATSIM traffic',''],['vatsimA','VATSIM ATC',''],
    ['ivaoP','IVAO traffic',''],['ivaoA','IVAO ATC','']]},
  {sec:'Overlays', items:[['route','Flight plan route',''],['rings','Range rings','25 · 50 · 100 NM'],['labels','Callsign labels','When zoomed in']]},
];
const MAP_ICONS={
  layers:`<path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/>`,
  follow:`<circle cx="12" cy="12" r="3"/><path d="M12 2v4M12 18v4M2 12h4M18 12h4"/>`,
  fit:`<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>`,
  zin:`<path d="M12 5v14M5 12h14"/>`, zout:`<path d="M5 12h14"/>`,
};
// great-circle destination point (deg / nm)
function destPoint(lat,lon,brg,nm){ const R=3440.065, d=nm/R, t=brg*Math.PI/180, p1=lat*Math.PI/180, l1=lon*Math.PI/180;
  const p2=Math.asin(Math.sin(p1)*Math.cos(d)+Math.cos(p1)*Math.sin(d)*Math.cos(t));
  const l2=l1+Math.atan2(Math.sin(t)*Math.sin(d)*Math.cos(p1),Math.cos(d)-Math.sin(p1)*Math.sin(p2));
  return [p2*180/Math.PI, ((l2*180/Math.PI+540)%360)-180]; }
const fl=ft=>ft>=18000?'FL'+String(Math.round(ft/100)).padStart(3,'0'):Math.round(ft).toLocaleString()+' FT';

async function renderMap(body){
  body.classList.add('nopad');
  const P=mapPrefs();
  const tb=(k,label)=>`<button class="mp-btn ${k==='follow'&&P.follow?'on':''}" data-t="${k}" title="${label}" aria-label="${label}"><svg ${SVG_ATTRS}>${MAP_ICONS[k]}</svg></button>`;
  body.innerHTML=`<div class="mp">
    <div id="efbMap" class="mp-map"></div>
    <div class="mp-tools">${tb('layers','Layers')}${tb('follow','Follow aircraft')}${tb('fit','Fit route')}
      <div class="mp-zoom">${tb('zin','Zoom in')}${tb('zout','Zoom out')}</div></div>
    <aside class="mp-panel hidden" id="mpPanel" aria-label="Map layers"></aside>
    <aside class="mp-card hidden" id="mpCard" aria-live="polite"></aside>
    <div class="mp-strip" id="mpStrip"></div>
    <div class="mp-radar hidden" id="mpRadar"><button id="mpPlay" aria-label="Play radar loop">▶</button>
      <input type="range" id="mpFrame" min="0" max="0" value="0" aria-label="Radar frame"><span class="mono" id="mpTime">--:--Z</span></div>
    <div class="mp-legend" id="mpLegend"></div>
  </div>`;
  try{ await loadLeaflet(); }catch{ body.innerHTML=`<div class="empty">The map needs an internet connection to load.<br>Your route and traffic will appear once online.</div>`; return; }
  if(activeApp!=='map') return;

  const start=(state.latitude||state.longitude)?[state.latitude,state.longitude]:[50,5];
  const map=L.map('efbMap',{zoomControl:false,worldCopyJump:true,minZoom:2,maxZoom:18,zoomSnap:.5}).setView(start,6);
  map.attributionControl.setPrefix(false);
  [['wx',250],['sectors',330],['wxv',360],['refLabels',380],['route',420]].forEach(([n,z])=>{ const p=map.createPane(n); p.style.zIndex=z; if(n==='refLabels')p.style.pointerEvents='none'; });
  const mapEl=$('#efbMap');

  // ── base map
  let baseL=null, refL=null;
  function setBase(k){ const b=BASEMAPS[k]||BASEMAPS.dark;
    if(baseL)map.removeLayer(baseL); if(refL)map.removeLayer(refL);
    baseL=L.tileLayer(esriUrl(b.base),{maxNativeZoom:16,maxZoom:18,attribution:'Esri'}).addTo(map);
    refL=L.tileLayer(esriUrl(b.ref),{maxNativeZoom:16,maxZoom:18,pane:'refLabels'}).addTo(map);
    mapEl.dataset.base=k; }
  setBase(P.base);
  map.attributionControl.addAttribution('RainViewer · aviationweather.gov · VATSpy &amp; SimAware data CC BY-SA 4.0 · VATSIM · IVAO');

  // ── canvas traffic layer (all aircraft targets)
  const cv=document.createElement('canvas'); cv.className='mp-canvas'; mapEl.appendChild(cv);
  const ctx=cv.getContext('2d'), planePath=new Path2D(PLANE);
  let targets=[], raf=0;
  function sizeCanvas(){ const s=map.getSize(), r=window.devicePixelRatio||1;
    cv.width=s.x*r; cv.height=s.y*r; cv.style.width=s.x+'px'; cv.style.height=s.y+'px'; ctx.setTransform(r,0,0,r,0,0); }
  function draw(){ raf=0; const s=map.getSize(), z=map.getZoom();
    ctx.clearRect(0,0,s.x,s.y);
    const sc=z<4?.28:z<6?.36:z<8?.46:.58, labels=P.labels&&z>=7;
    ctx.font='600 10.5px ui-monospace,SF Mono,Consolas,monospace'; ctx.lineWidth=1.2/sc;
    for(const t of targets){
      const p=map.latLngToContainerPoint([t.lat,t.lon]);
      if(p.x<-20||p.y<-20||p.x>s.x+20||p.y>s.y+20) continue;
      ctx.save(); ctx.translate(p.x,p.y); ctx.rotate((t.hdg||0)*Math.PI/180); ctx.scale(sc,sc);
      ctx.strokeStyle='rgba(8,10,16,.75)'; ctx.stroke(planePath); ctx.fillStyle=t.col; ctx.fill(planePath); ctx.restore();
      if(labels){ ctx.fillStyle='rgba(8,10,16,.7)'; ctx.fillText(t.label,p.x+10,p.y+5); ctx.fillStyle=t.col; ctx.fillText(t.label,p.x+9,p.y+4); }
    }
  }
  const redraw=()=>{ if(!raf) raf=requestAnimationFrame(draw); };
  sizeCanvas();
  map.on('move',redraw).on('zoomstart',()=>cv.style.opacity=0).on('zoomend',()=>{ cv.style.opacity=1; redraw(); })
     .on('resize',()=>{ sizeCanvas(); redraw(); });

  // ── data
  let net=null, company=[], rv=null;
  const L_={route:[], rings:[], own:null, vec:null, sectors:new Map(), aptBadges:[], sigmets:[], metars:[], radar:[], sat:null};
  const geom=new Map();
  function rebuildTargets(){
    targets=[];
    if(P.company) company.forEach(p=>targets.push({lat:p.lat,lon:p.lon,hdg:p.heading,col:COMPANY_COL,label:p.callsign||p.pilotName||'',kind:'company',o:p}));
    if(net) net.pilots.forEach(p=>{ if((p.network==='VATSIM'&&P.vatsimP)||(p.network==='IVAO'&&P.ivaoP))
      targets.push({lat:p.lat,lon:p.lon,hdg:p.headingDeg,col:NET_COL[p.network],label:p.callsign,kind:'net',o:p}); });
    redraw();
  }

  // ── ownship, route, rings
  function drawOwn(){
    const s=state||{}; if(!(s.latitude||s.longitude))return;
    const ll=[s.latitude,s.longitude], trk=s.trackDeg||s.headingDeg||0;
    const icon=L.divIcon({className:'mp-own',iconSize:[34,34],iconAnchor:[17,17],
      html:`<svg viewBox="-24 -24 48 48" style="transform:rotate(${trk}deg)"><path d="${PLANE}" fill="${OWN_COL}" stroke="#0a0d17" stroke-width="2"/></svg>`});
    if(L_.own) L_.own.setLatLng(ll).setIcon(icon);
    else L_.own=L.marker(ll,{icon,zIndexOffset:2000,keyboard:false}).addTo(map).on('click',()=>showOwnCard());
    // track vector: where the aircraft will be in 5 minutes at the current ground speed
    const nm=(s.groundKts||0)*5/60;
    if(L_.vec){ map.removeLayer(L_.vec); L_.vec=null; }
    if(nm>1) L_.vec=L.polyline([ll,destPoint(ll[0],ll[1],trk,nm)],{color:OWN_COL,weight:2,dashArray:'2 6',pane:'route',interactive:false}).addTo(map);
    L_.rings.forEach(r=>map.removeLayer(r)); L_.rings=[];
    if(P.rings) [25,50,100].forEach(n=>L_.rings.push(L.circle(ll,{radius:n*1852,color:'#9aa3b5',weight:1,opacity:.5,fill:false,dashArray:'4 6',pane:'route',interactive:false}).addTo(map)));
    if(P.follow) map.panTo(ll,{animate:true,duration:.6});
  }
  function drawRoute(){
    L_.route.forEach(l=>map.removeLayer(l)); L_.route=[];
    if(!P.route||!plan.hasPlan||!plan.waypoints?.length) return;
    const wps=plan.waypoints.filter(w=>w.latitude||w.longitude), pts=wps.map(w=>[w.latitude,w.longitude]);
    if(pts.length<2) return;
    L_.route.push(L.polyline(pts,{color:OWN_COL,weight:3,opacity:.9,pane:'route'}).addTo(map));
    wps.forEach((w,i)=>{ const end=i===0||i===wps.length-1;
      const m=L.circleMarker([w.latitude,w.longitude],{radius:end?6:3,color:end?'#fff':OWN_COL,weight:end?2:1.5,fillColor:end?OWN_COL:'#0a0d17',fillOpacity:1,pane:'route'})
        .bindTooltip(esc(end?(i===0?plan.departureIcao:plan.arrivalIcao):w.ident||''),{permanent:true,direction:'right',offset:[6,0],className:'mp-wpt'+(end?' apt':'')}).addTo(map);
      L_.route.push(m); });
  }
  function fitRoute(){ const r=L_.route[0]; if(r) map.fitBounds(r.getBounds(),{padding:[60,60],maxZoom:9}); else if(L_.own) map.setView(L_.own.getLatLng(),8); }
  const zoomCls=()=>{ const z=map.getZoom(); mapEl.classList.toggle('z-lo',z<6); mapEl.classList.toggle('z-mid',z<8); };
  map.on('zoomend',zoomCls); zoomCls();

  // ── networks: traffic + ATC
  async function loadNetwork(){
    if(!(P.vatsimP||P.vatsimA||P.ivaoP||P.ivaoA)) { net=null; rebuildTargets(); drawAtc(); renderPanel(); return; }
    try{ net=await getJSON('/efb/api/network'); }catch{ return; }
    if(activeApp!=='map') return;
    rebuildTargets(); await drawAtc(); renderPanel();
  }
  async function drawAtc(){
    L_.aptBadges.forEach(m=>map.removeLayer(m)); L_.aptBadges=[];
    const want=a=>(a.network==='VATSIM'&&P.vatsimA)||(a.network==='IVAO'&&P.ivaoA);
    const sectors=(net?.sectors||[]).filter(want);
    // fetch outlines we don't have yet (static — once per session)
    const missing=[...new Set(sectors.map(s=>s.key))].filter(k=>!geom.has(k));
    if(missing.length){ try{ const g=await getJSON('/efb/api/network/geometry?keys='+encodeURIComponent(missing.join(',')));
      Object.entries(g).forEach(([k,v])=>geom.set(k,v)); missing.forEach(k=>{ if(!geom.has(k)) geom.set(k,[]); }); }catch{} }
    if(activeApp!=='map') return;
    const live=new Set();
    sectors.forEach(s=>{ const id=s.network+'|'+s.key; live.add(id);
      const rings=geom.get(s.key)||[]; const col=NET_COL[s.network];
      let entry=L_.sectors.get(id);
      if(!entry&&rings.length){
        const poly=L.polygon(rings,{pane:'sectors',color:col,weight:s.kind==='FIR'?1.3:1.6,opacity:.85,fillColor:col,fillOpacity:s.kind==='FIR'?.07:.13,dashArray:s.kind==='TRACON'?'5 4':null})
          .addTo(map).on('click',e=>{ L.DomEvent.stop(e); showSectorCard(L_.sectors.get(id)?.s||s); });
        const lbl=L.marker([s.labelLat,s.labelLon],{pane:'sectors',interactive:false,keyboard:false,
          icon:L.divIcon({className:'mp-sec-lbl',html:`<span style="--c:${col}">${esc(s.callsigns[0]||s.name)}</span>`,iconSize:null})}).addTo(map);
        entry={poly,lbl}; L_.sectors.set(id,entry);
      }
      if(entry) entry.s=s;
    });
    for(const [id,e] of L_.sectors) if(!live.has(id)){ map.removeLayer(e.poly); map.removeLayer(e.lbl); L_.sectors.delete(id); }
    // airport badges: D G T A + ATIS letter, one badge per network per airport (VATSIM Radar style)
    const byApt=new Map();
    (net?.atcs||[]).filter(want).forEach(a=>{
      if(a.lat==null||a.lon==null) return;
      const aptLevel=['DEL','GND','TWR','ATIS'].includes(a.facility)||(a.facility==='APP'&&a.airportIcao);
      const key=aptLevel?a.network+'|'+(a.airportIcao||a.callsign):null;
      if(!key){ if(!a.sectorKeys?.length){ // unplaceable enroute station: a plain pin at its reported position
          L_.aptBadges.push(L.circleMarker([a.lat,a.lon],{radius:5,color:NET_COL[a.network],weight:2,fillOpacity:.6})
            .on('click',e=>{ L.DomEvent.stop(e); showAtcCard(a.callsign,[a]); }).addTo(map)); }
        return; }
      const g=byApt.get(key)||{icao:a.airportIcao||a.callsign.split('_')[0],network:a.network,lat:a.lat,lon:a.lon,list:[]};
      if(a.facility!=='APP'){ g.lat=a.lat; g.lon=a.lon; }   // TRACON label point isn't the airport
      g.list.push(a); byApt.set(key,g); });
    byApt.forEach(g=>{
      const has=f=>g.list.some(a=>a.facility===f), atis=g.list.find(a=>a.facility==='ATIS');
      const letters=[['DEL','D'],['GND','G'],['TWR','T'],['APP','A']].filter(([f])=>has(f))
        .map(([f,l])=>`<i style="--c:${FAC_COL[f]}">${l}</i>`).join('')+(atis?`<i class="atis" style="--c:${FAC_COL.ATIS}">${esc(atis.atisCode||'i')}</i>`:'');
      const m=L.marker([g.lat,g.lon],{keyboard:false,zIndexOffset:500,
        icon:L.divIcon({className:'mp-apt',iconSize:null,html:`<div style="--n:${NET_COL[g.network]}"><b>${esc(g.icao)}</b>${letters}</div>`})})
        .on('click',e=>{ L.DomEvent.stop(e); showAtcCard(`${g.icao} · ${g.network}`,g.list); }).addTo(map);
      L_.aptBadges.push(m); });
  }

  // ── weather: RainViewer radar loop + IR satellite
  let frameIdx=0, playIv=null;
  async function loadRainViewer(){ try{ rv=await fetch('https://api.rainviewer.com/public/weather-maps.json').then(r=>r.json()); }catch{ rv=null; } }
  async function drawRadar(){
    L_.radar.forEach(l=>map.removeLayer(l)); L_.radar=[]; stopLoop(); $('#mpRadar').classList.toggle('hidden',!P.radar);
    if(!P.radar) return;
    if(!rv) await loadRainViewer(); const frames=rv?.radar?.past||[];
    if(!frames.length||activeApp!=='map') return;
    L_.radar=frames.map((f,i)=>L.tileLayer(`${rv.host}${f.path}/256/{z}/{x}/{y}/2/1_1.png`,
      {pane:'wx',opacity:i===frames.length-1?.62:0,maxNativeZoom:7,maxZoom:18,tileSize:256}).addTo(map));
    const fr=$('#mpFrame'); fr.max=frames.length-1; showFrame(frames.length-1);
  }
  function showFrame(i){ const frames=rv?.radar?.past||[]; if(!frames.length)return;
    L_.radar.forEach((l,j)=>l.setOpacity(j===i?.62:0)); frameIdx=i; $('#mpFrame').value=i;
    const d=new Date(frames[i].time*1000); $('#mpTime').textContent=String(d.getUTCHours()).padStart(2,'0')+':'+String(d.getUTCMinutes()).padStart(2,'0')+'Z'+(i===frames.length-1?' · LATEST':''); }
  function stopLoop(){ if(playIv){ clearInterval(playIv); playIv=null; } const b=$('#mpPlay'); if(b){ b.textContent='▶'; b.setAttribute('aria-label','Play radar loop'); } }
  function toggleLoop(){ if(playIv){ stopLoop(); showFrame(L_.radar.length-1); return; }
    const b=$('#mpPlay'); b.textContent='❚❚'; b.setAttribute('aria-label','Pause radar loop'); let hold=0;
    playIv=setInterval(()=>{ const n=L_.radar.length; if(!n)return; if(frameIdx===n-1&&hold++<3)return; hold=0; showFrame((frameIdx+1)%n); },550); }
  async function drawSat(){
    if(L_.sat){ map.removeLayer(L_.sat); L_.sat=null; } if(!P.sat) return;
    if(!rv) await loadRainViewer(); const f=rv?.satellite?.infrared?.slice(-1)[0]; if(!f||activeApp!=='map') return;
    L_.sat=L.tileLayer(`${rv.host}${f.path}/256/{z}/{x}/{y}/0/0_0.png`,{pane:'wx',opacity:.5,maxNativeZoom:7,maxZoom:18}).addTo(map);
  }

  // ── weather: SIGMETs + METAR dots
  async function drawSigmets(){
    L_.sigmets.forEach(l=>map.removeLayer(l)); L_.sigmets=[]; if(!P.sigmet) return;
    let list=[]; try{ list=await getJSON('/efb/api/wx/sigmets'); }catch{ return; }
    if(activeApp!=='map'||!P.sigmet) return;
    list.forEach(s=>{ const c=hazCol(s.hazard||s.kind);
      L_.sigmets.push(L.polygon(s.rings,{pane:'wxv',color:c,weight:1.6,dashArray:'6 4',fillColor:c,fillOpacity:.1})
        .on('click',e=>{ L.DomEvent.stop(e); showSigmetCard(s); }).addTo(map)); });
    renderPanel();
  }
  let metarT=0;
  function queueMetars(){ clearTimeout(metarT); metarT=setTimeout(drawMetars,600); }
  async function drawMetars(){
    L_.metars.forEach(l=>map.removeLayer(l)); L_.metars=[];
    if(!P.metar||map.getZoom()<6){ renderPanel(); return; }
    const b=map.getBounds(); let list=[];
    try{ list=await getJSON(`/efb/api/wx/metars?minLat=${b.getSouth().toFixed(2)}&minLon=${b.getWest().toFixed(2)}&maxLat=${b.getNorth().toFixed(2)}&maxLon=${b.getEast().toFixed(2)}`); }catch{ return; }
    if(activeApp!=='map'||!P.metar) return;
    list.forEach(m=>{ if(!m.cat) return;
      L_.metars.push(L.circleMarker([m.lat,m.lon],{pane:'wxv',radius:5.5,color:'#0a0d17',weight:1.5,fillColor:CAT_COL[m.cat]||'#999',fillOpacity:.95})
        .bindTooltip(`${esc(m.id)} · ${esc(m.cat)}`,{direction:'top',className:'mp-tip'})
        .on('click',e=>{ L.DomEvent.stop(e); showCard(`${esc(m.id)} <span class="cat c-${esc(m.cat)}">${esc(m.cat)}</span>`,`<pre class="mp-raw">${esc(m.raw)}</pre>`); }).addTo(map)); });
    renderPanel();
  }
  map.on('moveend',queueMetars);

  // ── company traffic
  async function loadCompany(){
    if(!P.company){ company=[]; rebuildTargets(); return; }
    try{ company=(await getJSON('/efb/api/traffic')).filter(p=>p.pilotId!==msg.myPilotId); }catch{}
    rebuildTargets();
  }

  // ── cards
  const card=$('#mpCard');
  function showCard(title,html){ card.innerHTML=`<header><div class="t">${title}</div><button class="x" aria-label="Close">✕</button></header><div class="b">${html}</div>`;
    card.classList.remove('hidden'); card.querySelector('.x').onclick=()=>card.classList.add('hidden'); }
  const kv=(k,v)=>`<div class="kv"><span>${k}</span><b class="mono">${v}</b></div>`;
  function showPilotCard(t){ const p=t.o;
    if(t.kind==='company') return showCard(`${esc(p.callsign||p.pilotName||'Traffic')} <span class="net" style="--c:${COMPANY_COL}">AviatesAir</span>`,
      kv('Pilot',esc(p.pilotName||'—'))+kv('Route',`${esc(p.departureIcao||'----')} → ${esc(p.arrivalIcao||'----')}`)+kv('Altitude',fl(p.alt||0))+kv('Speed',(p.speed||0)+' KT')+kv('Heading',String(Math.round(p.heading||0)).padStart(3,'0')+'°')+kv('Phase',esc(p.phase||'—')));
    showCard(`${esc(p.callsign)} <span class="net" style="--c:${NET_COL[p.network]}">${esc(p.network)}</span>`,
      kv('Aircraft',esc(p.aircraftType||'—'))+kv('Route',`${esc(p.departureIcao||'----')} → ${esc(p.arrivalIcao||'----')}`)+kv('Altitude',fl(p.altitudeFt||0))+
      kv('Ground speed',(p.groundSpeedKts||0)+' KT')+kv('Heading',String(p.headingDeg||0).padStart(3,'0')+'°'));
  }
  function atcRows(list){ const order={ATIS:0,DEL:1,GND:2,TWR:3,APP:4,CTR:5,FSS:6};
    return [...list].sort((a,b)=>(order[a.facility]??9)-(order[b.facility]??9)).map(a=>`<div class="atc">
      <i style="--c:${FAC_COL[a.facility]||NET_COL[a.network]}">${esc(a.facility||a.position||'ATC')}</i><b class="mono">${esc(a.callsign)}</b>
      <span class="mono">${a.frequency?a.frequency.toFixed(3):''}</span>${a.atisCode?`<em>INFO ${esc(a.atisCode)}</em>`:''}</div>
      ${a.text?`<pre class="mp-raw">${esc(a.text)}</pre>`:''}`).join(''); }
  function showAtcCard(title,list){ showCard(esc(title),atcRows(list)); }
  function showSectorCard(s){ const list=(net?.atcs||[]).filter(a=>a.network===s.network&&s.callsigns.includes(a.callsign));
    showCard(`${esc(s.name)} <span class="net" style="--c:${NET_COL[s.network]}">${esc(s.network)} ${s.kind==='FIR'?'FIR':'APP'}</span>`,atcRows(list)); }
  function showSigmetCard(s){ const lv=(s.baseFt!=null||s.topFt!=null)?`${s.baseFt?fl(s.baseFt):'SFC'} – ${s.topFt?fl(s.topFt):'—'}`:'—';
    showCard(`${esc(s.kind||'SIGMET')} <span class="net" style="--c:${hazCol(s.hazard||s.kind)}">${esc(s.hazard||'')}</span>`,
      kv('FIR',esc(s.fir||'—'))+kv('Levels',lv)+kv('Valid',`${zt(s.validFrom)} – ${zt(s.validTo)}`)+`<pre class="mp-raw">${esc(s.raw)}</pre>`); }
  function showOwnCard(){ const s=state||{};
    showCard(`${esc(s.callsign||'Ownship')} <span class="net" style="--c:${OWN_COL}">YOU</span>`,
      kv('Route',`${esc(s.departureIcao||'----')} → ${esc(s.arrivalIcao||'----')}`)+kv('Altitude',fl(s.altitudeFt||0))+kv('Ground speed',Math.round(s.groundKts||0)+' KT')+
      kv('Track',String(Math.round(s.trackDeg||0)).padStart(3,'0')+'°')+kv('Remaining',s.distanceRemainingNm?Math.round(s.distanceRemainingNm)+' NM':'—')); }
  map.on('click',e=>{ // nearest aircraft target within 14 px, else close the card
    // positions are projected here rather than reusing the last draw's: a data refresh
    // replaces `targets`, and the redraw that would re-project them is rAF-deferred
    let best=null, bd=14*14; const p=e.containerPoint;
    for(const t of targets){ const q=map.latLngToContainerPoint([t.lat,t.lon]); const dx=q.x-p.x, dy=q.y-p.y, d=dx*dx+dy*dy; if(d<bd){bd=d;best=t;} }
    if(best) showPilotCard(best); else card.classList.add('hidden'); });

  // ── layers sheet
  const panel=$('#mpPanel');
  function counts(){ const c={};
    if(net){ ['VATSIM','IVAO'].forEach(n=>{ c[n+'P']=net.pilots.filter(p=>p.network===n).length;
      c[n+'A']=net.atcs.filter(a=>a.network===n&&a.facility!=='ATIS').length; }); }
    return {company:P.company?company.length:null, vatsimP:c.VATSIMP, vatsimA:c.VATSIMA, ivaoP:c.IVAOP, ivaoA:c.IVAOA,
      sigmet:P.sigmet?L_.sigmets.length:null, metar:P.metar?(map.getZoom()<6?'zoom in':L_.metars.length):null}; }
  function renderPanel(){
    const n=counts();
    panel.innerHTML=`<header><b>Layers</b><button class="x" aria-label="Close layers">✕</button></header>
      <div class="mp-sec"><h4>Base map</h4><div class="mp-seg">${Object.entries(BASEMAPS).map(([k,b])=>`<button data-base="${k}" class="${P.base===k?'on':''}">${b.name}</button>`).join('')}</div></div>
      ${MAP_LAYERS.map(g=>`<div class="mp-sec"><h4>${g.sec}</h4>${g.items.map(([k,name,sub])=>{
        const col=k.startsWith('vatsim')?NET_COL.VATSIM:k.startsWith('ivao')?NET_COL.IVAO:k==='company'?COMPANY_COL:null;
        const cnt=n[k]; const meta=cnt!=null&&cnt!==undefined?(typeof cnt==='number'?cnt.toLocaleString()+(k.endsWith('A')?' online':k==='sigmet'?' active':k==='metar'?' stations':' flying'):cnt):sub;
        return `<label class="mp-sw">${col?`<i class="sw" style="--c:${col}"></i>`:''}<span><b>${name}</b><small>${esc(meta||'')}</small></span>
          <input type="checkbox" data-l="${k}" ${P[k]?'checked':''}><em aria-hidden="true"></em></label>`;}).join('')}</div>`).join('')}
      ${net&&!net.hasSectorData&&(P.vatsimA||P.ivaoA)?`<p class="mp-note">Sector outlines unavailable (VATSpy data couldn't be downloaded) — ATC shows as pins.</p>`:''}`;
    panel.querySelector('.x').onclick=()=>{ panel.classList.add('hidden'); $('.mp-btn[data-t="layers"]').classList.remove('on'); };
    $$('#mpPanel [data-base]').forEach(b=>b.onclick=()=>{ P.base=b.dataset.base; saveMapPrefs(P); setBase(P.base); renderPanel(); });
    $$('#mpPanel [data-l]').forEach(i=>i.onchange=()=>{ P[i.dataset.l]=i.checked; saveMapPrefs(P); applyLayer(i.dataset.l); });
    renderLegend();
  }
  function applyLayer(k){
    if(k==='radar') drawRadar();
    else if(k==='sat') drawSat();
    else if(k==='sigmet') drawSigmets();
    else if(k==='metar') drawMetars();
    else if(k==='company') loadCompany();
    else if(k==='route') drawRoute();
    else if(k==='rings') drawOwn();
    else if(k==='labels') redraw();
    else if(['vatsimP','ivaoP'].includes(k)) (net?rebuildTargets():loadNetwork());
    else if(['vatsimA','ivaoA'].includes(k)) (net?drawAtc():loadNetwork());
    if(/^(vatsim|ivao)/.test(k)&&(P.vatsimP||P.vatsimA||P.ivaoP||P.ivaoA)&&!net) loadNetwork();
    renderPanel();
  }
  function renderLegend(){
    const parts=[];
    if(P.radar) parts.push(`<span class="lg-radar"><i></i>Radar light → heavy</span>`);
    if(P.metar) parts.push(Object.entries(CAT_COL).map(([k,c])=>`<span><i style="--c:${c}"></i>${k}</span>`).join(''));
    if(P.vatsimP||P.vatsimA) parts.push(`<span><i style="--c:${NET_COL.VATSIM}"></i>VATSIM</span>`);
    if(P.ivaoP||P.ivaoA) parts.push(`<span><i style="--c:${NET_COL.IVAO}"></i>IVAO</span>`);
    if(P.vatsimA||P.ivaoA) parts.push(Object.entries(FAC_COL).map(([k,c])=>`<span class="fac"><i style="--c:${c}"></i>${k}</span>`).join(''));
    $('#mpLegend').innerHTML=parts.join('<span class="sep"></span>');
    $('#mpLegend').classList.toggle('hidden',!parts.length);
  }

  // ── ownship data strip (flyPad / ND-style readout)
  function drawStrip(){ const s=state||{};
    if(!s.simConnected){ $('#mpStrip').innerHTML=`<span class="dim">SIMULATOR NOT CONNECTED</span>`; return; }
    const c=(k,v)=>`<span><small>${k}</small><b class="mono">${v}</b></span>`;
    $('#mpStrip').innerHTML=c('GS',Math.round(s.groundKts||0)+' KT')+c('ALT',fl(s.altitudeFt||0))+c('TRK',String(Math.round(s.trackDeg||0)).padStart(3,'0')+'°')+
      (s.arrivalIcao?c(s.arrivalIcao,s.distanceRemainingNm?Math.round(s.distanceRemainingNm)+' NM':'—'):'')+(s.etaUtc?c('ETA',zt(s.etaUtc)):''); }

  // ── tools
  $$('.mp-btn').forEach(b=>b.onclick=()=>{ const t=b.dataset.t;
    if(t==='layers'){ const open=panel.classList.toggle('hidden'); b.classList.toggle('on',!open); if(!open) renderPanel(); }
    else if(t==='follow'){ P.follow=!P.follow; saveMapPrefs(P); b.classList.toggle('on',P.follow); if(P.follow) drawOwn(); }
    else if(t==='fit'){ P.follow=false; saveMapPrefs(P); $('.mp-btn[data-t="follow"]').classList.remove('on'); fitRoute(); }
    else if(t==='zin') map.zoomIn(); else if(t==='zout') map.zoomOut(); });
  map.on('dragstart',()=>{ if(P.follow){ P.follow=false; saveMapPrefs(P); $('.mp-btn[data-t="follow"]').classList.remove('on'); } });
  $('#mpPlay').onclick=toggleLoop;
  $('#mpFrame').oninput=e=>{ stopLoop(); showFrame(+e.target.value); };

  // ── go
  drawRoute(); drawOwn(); drawStrip(); renderPanel();
  if(!(state.latitude||state.longitude)) fitRoute();
  loadCompany(); loadNetwork(); drawRadar(); drawSat(); drawSigmets(); drawMetars();
  const ivs=[
    setInterval(()=>{ drawOwn(); drawStrip(); },2000),
    setInterval(loadCompany,12000),
    setInterval(()=>{ if(P.vatsimP||P.vatsimA||P.ivaoP||P.ivaoA) loadNetwork(); },30000),
    setInterval(async()=>{ if(P.radar||P.sat){ await loadRainViewer(); if(!playIv){ drawRadar(); drawSat(); } } },600000),
    setInterval(()=>{ if(P.sigmet) drawSigmets(); },300000),
  ];
  appCleanup=()=>{ ivs.forEach(clearInterval); stopLoop(); clearTimeout(metarT); if(raf)cancelAnimationFrame(raf); try{ map.remove(); }catch{} };
}

// ── PILOT BRIEF APP ──
// The briefing package as an iPad document app (Files / Notes): the sections of the brief in a
// sidebar, one section open on the right. The OFP and the loadsheet stay paper, because that is
// what they are; the dispatch release, weather and NOTAMs are native lists and cards.
// Data comes from /efb/api/ofp (EfbBriefing), fetched on open — not pushed over the WS.
let pb={tab:'ofp', data:null, zoom:13, notamFilter:'ALL', notamRead:new Set(), wx:{}, loading:false, pane:'main'};
const PB_TABS=[
  {id:'ofp',   name:'Flight plan', c:'#2f7fd6', icon:`<path d="M6 3h9l4 4v14H6z"/><path d="M15 3v4h4"/><path d="M9 11h7M9 14.5h7M9 18h4"/>`},
  {id:'sum',   name:'Release',     c:'#34a853', icon:`<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 4V3h6v1M9 11l2 2 4-4M9 17h6"/>`},
  {id:'wx',    name:'Weather',     c:'#30b0c7', icon:`<path d="M7 18h10a4 4 0 0 0 .4-8A5.5 5.5 0 0 0 7 9.5 4.3 4.3 0 0 0 7 18Z"/>`},
  {id:'notam', name:'NOTAMs',      c:'#ff9500', icon:`<path d="M12 4 21 19H3Z"/><path d="M12 10v4M12 16.8v.2"/>`},
  {id:'docs',  name:'Documents',   c:'#8e8e93', icon:`<path d="M4 5.5C4 4.7 4.7 4 5.5 4H12v16H5.5A1.5 1.5 0 0 1 4 18.5Z"/><path d="M20 5.5c0-.8-.7-1.5-1.5-1.5H12v16h6.5a1.5 1.5 0 0 0 1.5-1.5Z"/>`},
];
const pbKey=d=>`efbBriefAccept:${d.departureIcao}-${d.arrivalIcao}-${d.generatedAtUtc}`;
const zt=iso=>{ if(!iso)return '—'; const d=new Date(iso); return isNaN(d)?'—':String(d.getUTCHours()).padStart(2,'0')+String(d.getUTCMinutes()).padStart(2,'0')+'Z'; };
const zdate=iso=>{ const d=iso?new Date(iso):null; if(!d||isNaN(d))return ''; return String(d.getUTCDate()).padStart(2,'0')+MONTHS[d.getUTCMonth()].toUpperCase()+String(d.getUTCFullYear()).slice(2); };
const hmm=m=>m>0?`${Math.floor(m/60)}:${String(m%60).padStart(2,'0')}`:'—';
const n0=v=>v?Math.round(v).toLocaleString('en-US'):'—';
const pbWide=()=>matchMedia('(min-width:761px)').matches;

async function loadBriefing(){
  pb.loading=true;
  try{ pb.data=await getJSON('/efb/api/ofp'); }catch{ pb.data=pb.data||{hasPlan:false,_err:true}; }
  pb.loading=false;
  if(pb.data?.hasPlan){ try{ pb.accepted=localStorage.getItem(pbKey(pb.data)); }catch{ pb.accepted=null; } }
}
async function renderPilotBrief(body){
  body=body||$('#appBody');
  body.classList.add('nopad');
  if(!pb.data){ body.innerHTML=`<div class="fx"><div class="empty" style="margin:auto">Loading the briefing…</div></div>`;
    await loadBriefing(); if(activeApp!=='ofp')return; }
  const d=pb.data, unread=(d.notams||[]).filter((_,i)=>!pb.notamRead.has(i)).length;
  if(!pbWide()&&!pb.paneSet){ pb.pane='side'; pb.paneSet=true; }
  body.innerHTML=`<div class="fx sv" data-pane="${pbWide()?'main':pb.pane}">
    <aside class="sv-side">
      <div class="sv-t"><h1>Pilot Brief</h1>${d.hasPlan?`<p><span class="brand">${esc(d.flightNumber||d.callsign||'')}</span> ${esc(d.departureIcao)} to ${esc(d.arrivalIcao)}</p>`:''}</div>
      <div class="gl">${PB_TABS.map(t=>`<button class="row ic sv-it${pb.tab===t.id?' on':''}" data-tab="${t.id}" ${d.hasPlan||t.id==='ofp'?'':'disabled'}>
        <span class="sq" style="--c:${t.c}"><svg viewBox="0 0 24 24">${t.icon}</svg></span><span class="k">${t.name}</span>
        ${t.id==='notam'&&d.hasPlan&&unread?`<span class="sv-badge" id="pbBadge">${unread}</span>`:''}${chev}</button>`).join('')}</div>
      ${d.hasPlan?`<p class="gl-f">OFP generated ${esc(zdate(d.generatedAtUtc))} at ${esc(zt(d.generatedAtUtc))}</p>`:''}
      <button class="bt sv-reload" id="pbReload"><svg viewBox="0 0 24 24">${IC.refresh}</svg>Reload the brief</button>
    </aside>
    <section class="sv-main">
      <header class="sv-bar"><button class="sv-back" id="pbBack"><svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6"/></svg>Brief</button>
        <h2>${d.hasPlan?({ofp:'Flight plan',sum:'Dispatch release',wx:'Weather',notam:'NOTAMs',docs:'Documents'})[pb.tab]:'Pilot Brief'}</h2><div class="sv-tools" id="pbTools"></div></header>
      <div class="sv-doc" id="pbDoc"></div>
    </section>
  </div>`;
  $$('#appBody .sv-it').forEach(b=>b.onclick=()=>{ pb.tab=b.dataset.tab; pb.pane='main'; renderPilotBrief(body); });
  $('#pbBack').onclick=()=>{ pb.pane='side'; $('#appBody .sv').dataset.pane='side'; };
  $('#pbReload').onclick=async()=>{ const b=$('#pbReload'); b.disabled=true; await loadBriefing(); if(activeApp==='ofp')renderPilotBrief(body); };
  const doc=$('#pbDoc');
  if(!d.hasPlan) return pbImport(doc);
  ({ofp:pbOfp,sum:pbSummary,wx:pbWeather,notam:pbNotams,docs:pbDocs}[pb.tab]||pbOfp)(doc,d);
}

// Empty state: import the latest SimBrief OFP. EFB-only (never reaches ACARS); a blank
// username uses the one saved in desktop Settings.
function pbImport(doc){
  doc.innerHTML=`<div class="w empty"><b>No briefing yet</b>Import your latest SimBrief OFP to get the flight plan, release, weather and NOTAMs. It uses the SimBrief account saved in the desktop app and fills the EFB only.
    <button class="bt pri" id="pbImport"><svg viewBox="0 0 24 24">${IC.down}</svg>Import from SimBrief</button>
    <p class="tone-bad" id="pbImpErr">${pb.data?._err?'Could not reach the desktop app.':''}</p></div>`;
  $('#pbImport').onclick=async()=>{
    const b=$('#pbImport'); b.disabled=true; b.lastChild.textContent='Importing…';
    const r=await postJSON('/efb/api/flightplan/import',{username:''}).catch(()=>({error:'Could not reach the desktop app.'}));
    if(!r||r.error){ b.disabled=false; b.lastChild.textContent='Import from SimBrief'; $('#pbImpErr').textContent=(r&&r.error)||'Import failed.'; return; }
    plan=r; pb.data=null; renderPilotBrief();
  };
}

// ── FLIGHT PLAN: the OFP text on paper. SimBrief's plan_text is HTML; it is rebuilt from
// text nodes (only bold survives) so nothing from the document can script the EFB, and
// SimBrief's page-break markers become separate sheets.
function sanitizeOfp(html){
  const root=new DOMParser().parseFromString(html||'','text/html').body;
  const walk=n=>{
    if(n.nodeType===3) return esc(n.nodeValue);
    if(n.nodeType!==1) return '';
    const tag=n.tagName, inner=[...n.childNodes].map(walk).join('');
    if(/^(SCRIPT|STYLE|IMG|SVG|IFRAME|OBJECT|NOSCRIPT)$/.test(tag)) return '';
    if(/page-break/i.test(n.getAttribute('style')||'')) return inner+'\f';
    if(tag==='BR') return '\n';
    if(/^(B|STRONG|H[1-6])$/.test(tag)) return `<b>${inner}</b>`;
    if(/^(DIV|P|TR|LI|TABLE|H[1-6])$/.test(tag)) return inner+(inner.endsWith('\n')?'':'\n');
    return inner;
  };
  return walk(root).split('\f').map(p=>p.replace(/^\n+|\s+$/g,'')).filter(Boolean);
}
function pbOfp(doc,d){
  $('#pbTools').innerHTML=`<div class="sg"><button id="pbZo" aria-label="Smaller text">A−</button><button id="pbZi" aria-label="Larger text">A+</button></div>`;
  const z=v=>{ pb.zoom=clamp(pb.zoom+v,10,19); doc.style.setProperty('--pbz',pb.zoom+'px'); };
  $('#pbZo').onclick=()=>z(-1); $('#pbZi').onclick=()=>z(1);
  doc.style.setProperty('--pbz',pb.zoom+'px');
  const pages=d.ofpHtml?sanitizeOfp(d.ofpHtml):[];
  doc.innerHTML=pages.length
    ? `<div class="paper-stack">${pages.map((p,i)=>`<article class="paper"><pre>${p}</pre><footer>${esc(d.departureIcao)}-${esc(d.arrivalIcao)}, page ${i+1} of ${pages.length}</footer></article>`).join('')}</div>`
    : `<div class="w empty"><b>No text version</b>This OFP layout only comes as a PDF. Open it from Documents.</div>`;
}

// The briefing's fuel and weights in the EFB's weight unit (they arrive in the OFP's own unit).
const PB_W=['tripFuel','contingencyFuel','alternateFuel','finalReserveFuel','extraFuel','minTakeoffFuel','taxiFuel','blockFuel',
  'landingFuel','planTakeoffFuel','zfw','maxZfw','tow','maxTow','ldw','maxLdw','cargo','payload'];
function pbUnits(d){
  const from=d.units==='LBS'?'LB':'KG', to=WU(); if(from===to) return d;
  const f=from==='KG'?LB_KG:1/LB_KG, o={...d,units:to==='LB'?'LBS':'KGS'};
  for(const k of PB_W) if(typeof o[k]==='number') o[k]=o[k]*f;
  return o;
}

// ── RELEASE: the dispatch release — the leg, fuel, weights against their limits, the route
// and the commander's acceptance.
function pbSummary(doc,d){
  $('#pbTools').innerHTML='';
  d=pbUnits(d);
  const U=d.units==='LBS'?'lb':'kg';
  const fuelRows=[['Trip',d.tripFuel,'#2f7fd6'],['Contingency',d.contingencyFuel,'#30b0c7'],[`Alternate${d.alternateIcao?' '+d.alternateIcao:''}`,d.alternateFuel,'#af52de'],
    ['Final reserve',d.finalReserveFuel,'#ff9500'],['Extra',d.extraFuel,'#34c759']];
  const fuelSum=fuelRows.reduce((s,r)=>s+(r[1]||0),0)||1;
  const nf=v=>d.blockFuel?Math.round(v||0).toLocaleString('en-US')+' '+U:'—';
  const wRow=(k,v,max)=>{ const pct=max?v/max*100:0, cls=pct>100?'tone-bad':pct>98?'tone-warn':'';
    return `<div class="row wt"><span class="k">${k}<small>${max?(max-v>=0?`${n0(max-v)} ${U} below the limit`:`${n0(v-max)} ${U} over the limit`):'No limit in the OFP'}</small></span>
      <span class="val ${cls}">${n0(v)}<small>of ${max?n0(max):'—'}</small></span><i class="wt-bar"><em style="width:${clamp(pct,0,100)}%" class="${cls?'over':''}"></em></i></div>`; };
  const wind=d.avgWindComponentKts, accepted=pb.accepted;
  doc.innerHTML=`<div class="rel">
    <div class="w rel-leg">
      <div class="prt"><div class="c"><b>${esc(d.departureIcao)}</b><span>${esc(d.departureName||'')}</span></div>
        <div class="rel-mid"><span>${hmm(d.blockTimeMin)} block, ${hmm(d.airTimeMin)} in the air</span><i></i><span>${d.distanceNm?n0(d.distanceNm)+' NM':''}</span></div>
        <div class="c r"><b>${esc(d.arrivalIcao)}</b><span>${esc(d.arrivalName||'')}</span></div></div>
      <div class="pcells">
        <div><small>Off blocks</small><b>${zt(d.scheduledOutUtc)}</b></div><div><small>On blocks</small><b>${zt(d.scheduledInUtc)}</b></div>
        <div><small>Runways</small><b>${esc(d.departureRunway||'—')}, ${esc(d.arrivalRunway||'—')}</b></div>
        <div><small>Cruise</small><b>${d.cruiseAltitudeFt?flLevel(d.cruiseAltitudeFt):'—'}</b></div><div><small>Cost index</small><b>${d.costIndex??'—'}</b></div>
        <div><small>Average wind</small><b>${wind?(wind>0?'+':'−')+Math.abs(wind)+' kt':'—'}</b></div>
        <div><small>Aircraft</small><b>${esc(d.aircraftType||'—')} ${esc(d.registration||'')}</b></div>
      </div>
    </div>
    <div class="rel-g">
      <div><div class="gl-h">Fuel</div><div class="gl">
        <div class="fbar">${fuelRows.map(r=>r[1]?`<i style="flex:${r[1]/fuelSum};background:${r[2]}" title="${r[0]}"></i>`:'').join('')}</div>
        ${fuelRows.map(r=>`<div class="row"><span class="k"><i class="dot" style="background:${r[2]}"></i>${r[0]}</span><span class="val">${nf(r[1])}</span></div>`).join('')}
        <div class="row strong"><span class="k">Minimum takeoff fuel</span><span class="val">${nf(d.minTakeoffFuel)}</span></div>
        <div class="row"><span class="k">Taxi</span><span class="val">${nf(d.taxiFuel)}</span></div>
        <div class="row strong"><span class="k">Block fuel</span><span class="val tone-blue">${nf(d.blockFuel)}</span></div>
        <div class="row"><span class="k">Estimated landing fuel</span><span class="val">${nf(d.landingFuel)}</span></div></div></div>
      <div><div class="gl-h">Weights</div><div class="gl">${wRow('Zero fuel weight',d.zfw,d.maxZfw)}${wRow('Takeoff weight',d.tow,d.maxTow)}${wRow('Landing weight',d.ldw,d.maxLdw)}
        ${rowHtml('Passengers',d.paxCount||'—')}${rowHtml('Cargo',n0(d.cargo)+' '+U)}${rowHtml('Payload',n0(d.payload)+' '+U)}${rowHtml('MAC at takeoff',d.macTowPercent!=null?d.macTowPercent.toFixed(1)+'%':'—')}</div>
        <div class="gl-h">Route</div><div class="gl"><div class="route mono"><b>${esc(d.departureIcao)}/${esc(d.departureRunway||'--')}</b> ${d.sid?`<em>${esc(d.sid)}</em> `:''}${esc(d.route||'')} ${d.star?`<em>${esc(d.star)}</em> `:''}<b>${esc(d.arrivalIcao)}/${esc(d.arrivalRunway||'--')}</b></div></div></div>
    </div>
    <div class="w accept${accepted?' done':''}"><div><b>Commander's acceptance</b><p>${accepted?`Accepted ${esc(accepted)}. Flight plan, weather and NOTAMs reviewed.`:'I have reviewed the flight plan, weather and NOTAMs for this flight.'}</p></div>
      <button class="bt ${accepted?'':'pri'}" id="pbAccept" ${accepted?'disabled':''}>${accepted?'Accepted':'Accept the brief'}</button></div>
  </div>`;
  const a=$('#pbAccept'); if(a&&!accepted) a.onclick=()=>{ const now=new Date(), s=`${zdate(now.toISOString())} at ${zt(now.toISOString())}`;
    pb.accepted=s; try{ localStorage.setItem(pbKey(d),s); }catch{} pbSummary(doc,d); };
}

// ── WEATHER: DEP / DEST / ALTN with flight category, decoded rows, formatted TAF and a
// live refresh through the host's METAR/TAF proxy.
function wxCategory(m){
  if(!m) return null;
  const t=m.toUpperCase().split(/\s+/);
  let vis=null, ceil=null;
  if(t.includes('CAVOK')) vis=10000;
  for(const x of t){
    if(vis==null&&/^\d{4}$/.test(x)&&!/^\d{4}Z$/.test(x)) vis=+x;
    const sm=x.match(/^(P|M)?(\d+)(?:\/(\d+))?SM$/); if(sm&&vis==null) vis=(sm[3]?+sm[2]/+sm[3]:+sm[2])*1609;
    const c=x.match(/^(BKN|OVC|VV)(\d{3})/); if(c){ const f=+c[2]*100; ceil=ceil==null?f:Math.min(ceil,f); }
  }
  if(vis==null&&ceil==null) return null;
  const v=vis??99999, c=ceil??99999;
  const cat=c<500||v<1609?'LIFR':c<1000||v<4828?'IFR':c<=3000||v<=8047?'MVFR':'VFR';
  return {cat, vis, ceil};
}
const fmtTaf=t=>esc(t||'').replace(/\s+(FM\d{6}|TEMPO|BECMG|PROB\d{2}(?:\s+TEMPO)?)/g,'\n  $1');
function pbWeather(doc,d){
  $('#pbTools').innerHTML=`<button class="bt" id="pbWxLive" style="min-height:40px"><svg viewBox="0 0 24 24">${IC.refresh}</svg>Update live</button>`;
  const st=[['Departure',d.departureIcao,d.departureName,d.departureMetar,d.departureTaf],['Destination',d.arrivalIcao,d.arrivalName,d.arrivalMetar,d.arrivalTaf],
            ['Alternate',d.alternateIcao,d.alternateName,d.alternateMetar,d.alternateTaf]].filter(s=>s[1]);
  doc.innerHTML=st.map(([role,icao,name,metar,taf])=>{
    const live=pb.wx[icao], strip=x=>(x||'').trim().replace(/^(METAR|SPECI|TAF)\s+/,'');
    const m=strip(live?.metar||metar), tf=strip(live?.taf||taf), c=wxCategory(m), x=parseMetar(m), wd=wxWords(m);
    return `<div class="gl-h">${role}, ${esc(icao)} ${esc(phPlace(name||''))}${c?` <span class="cat ${c.cat}">${c.cat}</span>`:''}<span class="gl-hr">${live?'Live '+zt(live.at):'From the OFP'}</span></div>
      <div class="gl">${rowHtml('Sky',esc(wd.head))}${rowHtml('Wind',x.windKts!=null?`${/VRB/.test(m)?'Variable':String(x.windDir).padStart(3,'0')+'°'} at ${x.windKts} kt${x.gustKts?', gusting '+x.gustKts:''}`:'—')}
        ${rowHtml('Visibility',c&&c.vis!=null?(c.vis>=9999?'10 km or more':c.vis>=5000?Math.round(c.vis/1000)+' km':Math.round(c.vis)+' m'):'—')}
        ${rowHtml('Ceiling',c?(c.ceil!=null?fmtN(c.ceil)+' ft':'None'):'—')}${rowHtml('Temperature, dew point',x.tempC!=null?`${x.tempC}°, ${x.dewC}°`:'—')}${rowHtml('QNH',x.qnh!=null?x.qnh+' hPa':'—')}
        <div class="metar-raw"><small>METAR</small>${m?colorMetar(m,icao):'Not available'}</div>
        <div class="metar-raw taf"><small>TAF</small>${tf?fmtTaf(tf):'Not available'}</div></div>`;}).join('');
  $('#pbWxLive').onclick=async()=>{ const b=$('#pbWxLive'); b.disabled=true;
    await Promise.all(st.map(async s=>{ try{ const r=await getJSON('/efb/api/weather?icao='+s[1]);
      if(r.metar||r.taf) pb.wx[s[1]]={metar:r.metar,taf:r.taf,at:new Date().toISOString()}; }catch{} }));
    if(activeApp==='ofp'&&pb.tab==='wx') pbWeather(doc,d); };
}

// ── NOTAMS: parsed ICAO-format fields when present (A/B/C/E), filter by airport, tap to
// mark as read — the unread count shows in the sidebar like a real briefing app.
function parseNotam(t){
  const s=String(t||''), f=k=>{ const m=s.match(new RegExp('\\b'+k+'\\)\\s*([\\s\\S]*?)(?=\\s[A-G]\\)\\s|$)')); return m?m[1].trim():''; };
  const id=(s.match(/\b([A-Z]\d{4}\/\d{2})\b/)||[])[1]||'';
  const loc=(f('A').match(/[A-Z]{4}/)||[])[0]||(s.match(/^\s*([A-Z]{4})\b/)||[])[1]||'';
  const dt=v=>{ const m=String(v).match(/^(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})/); return m?`${m[3]} ${MONTHS[+m[2]-1]||''} ${m[4]}${m[5]}Z`:v; };
  const c=f('C'), to=!c?'':/PERM/.test(c)?'permanent':dt(c)+(/\bEST\b/.test(c)?' (estimated)':'');
  return {id, loc, from:f('B')?dt(f('B')):'', to, text:f('E')||s};
}
function pbNotams(doc,d){
  const all=(d.notams||[]).map((t,i)=>({...parseNotam(t),i}));
  const role=n=>n.loc===d.departureIcao?'DEP':n.loc===d.arrivalIcao?'DEST':n.loc===d.alternateIcao?'ALTN':'OTHER';
  const RL={ALL:'All',DEP:'Departure',DEST:'Destination',ALTN:'Alternate',OTHER:'En route'};
  const counts=all.reduce((c,n)=>(c[role(n)]=(c[role(n)]||0)+1,c),{});
  const chips=['ALL','DEP','DEST','ALTN','OTHER'].filter(k=>k==='ALL'||counts[k]);
  $('#pbTools').innerHTML='';
  const list=all.filter(n=>pb.notamFilter==='ALL'||role(n)===pb.notamFilter);
  const read=all.filter(n=>pb.notamRead.has(n.i)).length;
  doc.innerHTML=!all.length?`<div class="w empty"><b>No NOTAMs</b>None were included with this OFP.</div>`:`
    <div class="sg" style="margin-bottom:12px">${chips.map(k=>`<button data-f="${k}" class="${pb.notamFilter===k?'on':''}">${RL[k]} ${k==='ALL'?all.length:counts[k]}</button>`).join('')}</div>
    <div class="nt-head"><span>${read} of ${all.length} read</span><button class="bt" id="pbAllRead" style="min-height:36px">Mark all as read</button></div>
    <div class="nt">${list.map(n=>`<button class="w nt-c${pb.notamRead.has(n.i)?' read':''}" data-i="${n.i}" aria-pressed="${pb.notamRead.has(n.i)}">
      <span class="nt-h"><span class="nt-r r-${role(n)}">${RL[role(n)]}</span><b>${esc(n.loc||'----')}</b>${n.id?`<span>${esc(n.id)}</span>`:''}
        <i class="nt-ck"><svg viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg></i></span>
      ${n.from?`<span class="nt-t">${esc(n.from)} to ${esc(n.to||'further notice')}</span>`:''}
      <span class="nt-x mono">${esc(n.text)}</span></button>`).join('')}</div>`;
  $$('#pbDoc [data-f]').forEach(b=>b.onclick=()=>{ pb.notamFilter=b.dataset.f; pbNotams(doc,d); });
  $$('#pbDoc .nt-c').forEach(el=>el.onclick=()=>{ const i=+el.dataset.i; pb.notamRead.has(i)?pb.notamRead.delete(i):pb.notamRead.add(i); pbNotams(doc,d); pbBadge(); });
  const ar=$('#pbAllRead'); if(ar) ar.onclick=()=>{ all.forEach(n=>pb.notamRead.add(n.i)); pbNotams(doc,d); pbBadge(); };
}
function pbBadge(){ const n=(pb.data?.notams||[]).filter((_,i)=>!pb.notamRead.has(i)).length, t=$('.sv-it[data-tab="notam"]');
  if(!t)return; let b=t.querySelector('.sv-badge'); if(!n){ b?.remove(); return; } if(!b){ b=document.createElement('span'); b.className='sv-badge'; t.insertBefore(b,t.querySelector('.chev')); } b.textContent=n; }

// ── DOCUMENTS: the SimBrief PDF (in-viewer, with an external fallback) and a preliminary
// loadsheet computed from the OFP weights — same %MAC model as the Hoppies ACARS loadsheet.
function pbDocs(doc,d){
  const sel=pb.docSel||(d.hasPdf?'pdf':'ls'); pb.docSel=sel;
  $('#pbTools').innerHTML=sel==='pdf'&&d.pdfUrl?`<a class="bt" style="min-height:40px" href="${esc(d.pdfUrl)}" target="_blank" rel="noopener">Open in a new tab</a>`:'';
  const seg=`<div class="sg" style="max-width:360px;margin-bottom:14px">${d.hasPdf?`<button data-d="pdf" class="${sel==='pdf'?'on':''}">OFP as PDF</button>`:''}<button data-d="ls" class="${sel==='ls'?'on':''}">Loadsheet</button></div>`;
  if(sel==='pdf'&&d.pdfUrl){ doc.innerHTML=seg+`<iframe class="pdf" src="${esc(d.pdfUrl)}" title="SimBrief OFP PDF"></iframe>`; }
  else {
    d=pbUnits(d);
    const U=d.units==='LBS'?'LB':'KG', r=v=>String(v?Math.round(v).toLocaleString('en-US'):'—').padStart(8,' ');
    const dow=d.zfw&&d.payload?d.zfw-d.payload:0;
    const lim=[d.maxZfw&&d.maxZfw-d.zfw, d.maxTow&&d.maxTow-d.tow, d.maxLdw&&d.maxLdw-d.ldw].filter(v=>typeof v==='number'&&v);
    const under=lim.length?Math.min(...lim):null;
    const line=(k,v,extra='')=>`${k.padEnd(24,' ')}${v}${extra}`;
    const txt=[
      `<b>LOADSHEET  PRELIMINARY</b>`,
      esc(`${(d.callsign||d.flightNumber||'').padEnd(10,' ')}${d.departureIcao} ${d.arrivalIcao}   ${(d.aircraftType||'').padEnd(5,' ')}${d.registration||''}   ${zdate(d.scheduledOutUtc)}`),
      `ALL WEIGHTS IN ${U==='KG'?'KILOGRAMS':'POUNDS'}`,'',
      line('PASSENGERS', String(d.paxCount||'—').padStart(8,' ')),
      line('CARGO / BAGGAGE', r(d.cargo)),
      line('TOTAL TRAFFIC LOAD', r(d.payload)),
      line('DRY OPERATING WEIGHT', r(dow)),
      line('ZERO FUEL WEIGHT ACTUAL', r(d.zfw), `   MAX ${r(d.maxZfw).trim()}`),
      line('TAKE OFF FUEL', r(d.planTakeoffFuel||(d.blockFuel-d.taxiFuel))),
      line('TAKE OFF WEIGHT  ACTUAL', r(d.tow), `   MAX ${r(d.maxTow).trim()}`),
      line('TRIP FUEL', r(d.tripFuel)),
      line('LANDING WEIGHT   ACTUAL', r(d.ldw), `   MAX ${r(d.maxLdw).trim()}`),'',
      under!=null?line('UNDERLOAD BEFORE LMC', r(under)):'',
      `MACZFW  ${d.macZfwPercent!=null?d.macZfwPercent.toFixed(1):'--.-'}      MACTOW  ${d.macTowPercent!=null?d.macTowPercent.toFixed(1):'--.-'}`,'',
      `PREPARED BY  AVIATESAIR DISPATCH (EFB)`,
      `<span class="dim">Computed from the OFP. %MAC is an approximate model, not certified W&amp;B.</span>`,
    ].filter(x=>x!==null).join('\n');
    doc.innerHTML=seg+`<div class="paper-stack"><article class="paper"><pre>${txt}</pre></article></div>`;
  }
  $$('#pbDoc [data-d]').forEach(b=>b.onclick=()=>{ pb.docSel=b.dataset.d; pbDocs(doc,pb.data); });
}

// ── WEATHER APP (ATIS · METAR · TAF) ──
// One station at a time, in the Navigraph / ForeFlight / flyPad weather idiom:
//   Stations — DEP / ARR / ALTN from the OFP, any ICAO by search, recently viewed (per device).
//   ATIS     — every online ATIS for the airport on VATSIM and IVAO (separate arrival/departure
//              ATIS too) with letter + frequency; a NEW INFORMATION alert when the letter changes;
//              a METAR-generated ATIS when nothing is online; the airport's online controllers.
//   METAR    — fully decoded: flight category, wind dial drawn over the real runways, visibility
//              and RVR, weather, a cloud-layer chart, temperature/dew point, QNH; a per-runway
//              head/crosswind table with the into-wind runway marked.
//   TAF      — decoded into an hour-by-hour flight-category timeline (prevailing + TEMPO/PROB),
//              with the OFP's ETA / ETD marked and the forecast at that time spelled out.
//   Hazards  — SIGMETs / AIRMETs whose area covers the station.
// Refreshes every 2 minutes while open.
const WX_RECENT='efbWxRecent';
let wx={sel:null, data:{}, sig:null, sigAt:0, atisSeen:{}, atisTab:{}, timer:null, alert:null};
const WX_CAT={VFR:'#3ddc84',MVFR:'#4D9BEC',IFR:'#ff5a5a',LIFR:'#d946ef'};
const WX_TXT={MI:'shallow',PR:'partial',BC:'patches of',DR:'low drifting',BL:'blowing',FZ:'freezing',
  DZ:'drizzle',RA:'rain',SN:'snow',SG:'snow grains',IC:'ice crystals',PL:'ice pellets',GR:'hail',GS:'small hail',UP:'unknown precipitation',
  BR:'mist',FG:'fog',FU:'smoke',VA:'volcanic ash',DU:'dust',SA:'sand',HZ:'haze',PY:'spray',PO:'dust whirls',SQ:'squalls',FC:'funnel cloud',SS:'sandstorm',DS:'duststorm'};
const WX_COV={FEW:'Few',SCT:'Scattered',BKN:'Broken',OVC:'Overcast'};

// ── decoding ──
function wxPhen(t){
  const m=t.match(/^(\+|-|VC)?((?:MI|PR|BC|DR|BL|SH|TS|FZ)?)((?:DZ|RA|SN|SG|IC|PL|GR|GS|UP)*|BR|FG|FU|VA|DU|SA|HZ|PY|PO|SQ|FC|SS|DS)$/);
  if(!m||!(m[2]||m[3])) return null;
  const ph=(m[3].match(/.{2}/g)||[]).map(c=>WX_TXT[c]||c), d=m[2];
  let s=(d&&d!=='SH'&&d!=='TS'?WX_TXT[d]+' ':'')+ph.join(' and ');
  if(d==='SH') s=(s||'rain')+' showers'; if(d==='TS') s='thunderstorm'+(s?' with '+s:'');
  const int=m[1]==='-'?'light ':m[1]==='+'?'heavy ':''; return {code:t,text:(int+s+(m[1]==='VC'?' in the vicinity':'')).trim(),bad:/TS|FZ|GR|\+|FC|SQ|VA|FG|SS|DS|PL/.test(t)};
}
// conditions shared by METAR bodies and TAF groups
function wxCond(toks){
  const c={wind:null,vis:null,wx:[],clouds:[],vv:null,cavok:false,nsw:false};
  for(let i=0;i<toks.length;i++){ const t=toks[i]; let m;
    if((m=t.match(/^(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?(KT|MPS|KMH)$/))){ const k=m[4]==='MPS'?1.944:m[4]==='KMH'?.54:1;
      c.wind={vrb:m[1]==='VRB',dir:m[1]==='VRB'?null:+m[1],kt:Math.round(+m[2]*k),gust:m[3]?Math.round(+m[3]*k):null}; }
    else if((m=t.match(/^(\d{3})V(\d{3})$/))&&c.wind){ c.wind.from=+m[1]; c.wind.to=+m[2]; }
    else if(t==='CAVOK'){ c.cavok=true; c.vis={m:10000,sm:10,plus:true}; }
    else if(/^\d{4}$/.test(t)){ const v=+t; c.vis={m:v,sm:v/1609.34,plus:v===9999}; }
    else if(/^\d$/.test(t)&&/^\d\/\dSM$/.test(toks[i+1]||'')){ const [a,b]=toks[i+1].replace('SM','').split('/'); c.vis={sm:+t+ +a/+b}; c.vis.m=c.vis.sm*1609.34; i++; }
    else if((m=t.match(/^([PM])?(\d+)(?:\/(\d+))?SM$/))){ const sm=m[3]?+m[2]/+m[3]:+m[2]; c.vis={sm,m:sm*1609.34,plus:m[1]==='P'}; }
    else if((m=t.match(/^(FEW|SCT|BKN|OVC)(\d{3}|\/\/\/)(CB|TCU|\/\/\/)?$/))) c.clouds.push({cov:m[1],ft:m[2]==='///'?null:+m[2]*100,type:m[3]&&m[3]!=='///'?m[3]:''});
    else if((m=t.match(/^VV(\d{3}|\/\/\/)$/))) c.vv=m[1]==='///'?0:+m[1]*100;
    else if(/^(NSC|NCD|SKC|CLR)$/.test(t)) c.clouds=[];
    else if(t==='NSW') c.nsw=true;
    else { const p=wxPhen(t); if(p) c.wx.push(p); }
  }
  return c;
}
const wxCeiling=c=>{ if(c.vv!=null) return c.vv; const l=c.clouds.filter(x=>(x.cov==='BKN'||x.cov==='OVC')&&x.ft!=null).map(x=>x.ft); return l.length?Math.min(...l):null; };
function wxCat(c){ if(!c||(!c.vis&&!c.clouds.length&&c.vv==null&&!c.cavok)) return null;
  const sm=c.vis?c.vis.sm:10, ceil=wxCeiling(c)??99999;
  return ceil<500||sm<1?'LIFR':ceil<1000||sm<3?'IFR':ceil<=3000||sm<=5?'MVFR':'VFR'; }
function wxMetar(raw){
  if(!raw) return null; const T=raw.replace(/=+$/,'').trim().split(/\s+/); const o={raw,speci:false,auto:false,rvr:[],trend:'',rmk:''};
  let i=0; if(/^(METAR|SPECI)$/.test(T[0])){ o.speci=T[0]==='SPECI'; i++; } o.station=T[i++];
  const tm=(T[i]||'').match(/^(\d{2})(\d{2})(\d{2})Z$/); if(tm){ o.day=+tm[1]; o.hour=+tm[2]; o.min=+tm[3]; i++; }
  const body=[]; for(;i<T.length;i++){ const t=T[i]; let m;
    if(t==='RMK'){ o.rmk=T.slice(i+1).join(' '); break; }
    if(/^(NOSIG|BECMG|TEMPO)$/.test(t)){ const r=T.indexOf('RMK',i); o.trend=T.slice(i,r<0?T.length:r).join(' '); if(r>=0) o.rmk=T.slice(r+1).join(' '); break; }
    if(t==='AUTO'||t==='COR'){ o.auto=o.auto||t==='AUTO'; continue; }
    if((m=t.match(/^R(\d{2}[LCR]?)\/([PM]?)(\d{4})(?:V([PM]?)(\d{4}))?(FT)?\/?([UDN])?$/))){ o.rvr.push({rwy:m[1],min:+m[3],max:m[5]?+m[5]:null,ft:!!m[6],trend:m[7]||'',pfx:m[2]}); continue; }
    if((m=t.match(/^(M?\d{2})\/(M?\d{2})?$/))){ o.temp=+m[1].replace('M','-'); if(m[2]) o.dew=+m[2].replace('M','-'); continue; }
    if((m=t.match(/^Q(\d{4})$/))){ o.qnh=+m[1]; o.inhg=+(o.qnh*0.02953).toFixed(2); continue; }
    if((m=t.match(/^A(\d{4})$/))){ o.inhg=+m[1]/100; o.qnh=Math.round(o.inhg/0.02953); continue; }
    body.push(t); }
  Object.assign(o,wxCond(body)); o.cat=wxCat(o); o.ceiling=wxCeiling(o);
  if(o.day!=null){ const n=new Date(); let d=new Date(Date.UTC(n.getUTCFullYear(),n.getUTCMonth(),o.day,o.hour,o.min)); if(d-n>2*864e5) d=new Date(Date.UTC(n.getUTCFullYear(),n.getUTCMonth()-1,o.day,o.hour,o.min)); o.obs=d; }
  return o;
}
function wxTaf(raw){
  if(!raw) return null; const T=raw.replace(/=+/g,' ').replace(/\s+/g,' ').trim().split(' ');
  let i=0; while(/^(TAF|AMD|COR)$/.test(T[i])) i++; const station=T[i++];
  const n=new Date(); const iss=(T[i]||'').match(/^(\d{2})(\d{2})(\d{2})Z$/); if(iss) i++;
  const baseDay=iss?+iss[1]:n.getUTCDate();
  const at=(dd,hh,mm=0)=>{ let mo=n.getUTCMonth(); if(dd<baseDay-10) mo++; else if(dd>baseDay+20) mo--; return new Date(Date.UTC(n.getUTCFullYear(),mo,dd,0,0)+((+hh)*60+(+mm))*6e4); };
  const per=t=>{ const m=t.match(/^(\d{2})(\d{2})\/(\d{2})(\d{2})$/); return m?[at(+m[1],+m[2]),at(+m[3],+m[4])]:null; };
  const vp=per(T[i]||''); if(vp) i++; if(!vp) return {station,raw,groups:[]};
  const groups=[]; let cur={type:'BASE',from:vp[0],to:vp[1],toks:[]};
  const push=()=>{ cur.cond=wxCond(cur.toks); groups.push(cur); };
  for(;i<T.length;i++){ const t=T[i]; let m;
    if(t==='RMK') break;
    if(/^T[XN]M?\d{2}\/\d{4}Z$/.test(t)) continue;
    if((m=t.match(/^FM(\d{2})(\d{2})(\d{2})$/))){ push(); cur={type:'FM',from:at(+m[1],+m[2],+m[3]),to:vp[1],toks:[]}; continue; }
    if(t==='BECMG'||t==='TEMPO'){ if(cur.type==='PROB'&&t==='TEMPO'&&!cur.toks.length&&!cur.from){ cur.type='PROB TEMPO'; continue; } push(); cur={type:t,toks:[]}; continue; }
    if((m=t.match(/^PROB(30|40)$/))){ push(); cur={type:'PROB',prob:+m[1],toks:[]}; continue; }
    const p=per(t); if(p&&!cur.toks.length&&!cur.from){ [cur.from,cur.to]=p; continue; }
    cur.toks.push(t); }
  push();
  // FM groups end where the next FM starts
  const fms=groups.filter(g=>g.type==='FM'||g.type==='BASE'); fms.forEach((g,k)=>{ if(fms[k+1]) g.to=fms[k+1].from; });
  groups.forEach(g=>{ g.cat=wxCat(g.cond); });
  return {station,raw,from:vp[0],to:vp[1],groups};
}
const wxMerge=(a,b)=>({...a, wind:b.wind||a.wind, vis:b.vis||a.vis, wx:b.wx.length||b.nsw?b.wx:a.wx, clouds:b.clouds.length||b.cavok||b.vv!=null?b.clouds:a.clouds, vv:b.vv!=null?b.vv:(b.clouds.length?null:a.vv), cavok:b.cavok||(b.vis||b.clouds.length?false:a.cavok)});
// prevailing + temporary conditions at time t
function wxTafAt(taf,t){
  if(!taf||!taf.groups.length) return null;
  let base=null; for(const g of taf.groups) if((g.type==='BASE'||g.type==='FM')&&g.from<=t) base=g;
  if(!base) base=taf.groups[0];
  let prev={...base.cond}; for(const g of taf.groups) if(g.type==='BECMG'&&g.from&&g.from<=t&&g.from>=base.from) prev=wxMerge(prev,g.cond);
  const temp=taf.groups.filter(g=>/TEMPO|PROB/.test(g.type)&&g.from&&g.from<=t&&g.to>t).map(g=>({g,cond:wxMerge(prev,g.cond)}));
  return {prev,cat:wxCat(prev),temp:temp.map(x=>({type:x.g.type,prob:x.g.prob,cat:wxCat(x.cond),cond:x.g.cond,raw:x.g.toks.join(' ')}))};
}
const wxRank={VFR:0,MVFR:1,IFR:2,LIFR:3};

// ── helpers ──
const wxZ=d=>d?d.toISOString().substr(11,5).replace(':','')+'Z':'';
const wxAge=d=>{ if(!d) return ''; const m=Math.round((Date.now()-d)/6e4); return m<1?'just now':m<60?`${m} min ago`:`${Math.floor(m/60)} h ${m%60} min ago`; };
function wxTok(t){ let w,c='';
  if((w=t.match(/^(\d{3}|VRB)(\d{2,3})(?:G(\d{2,3}))?(KT|MPS)$/))){ const v=Math.max(+w[2],+(w[3]||0)); c=v>=35?'r':v>=25?'a':''; }
  else if(/^\d{4}$/.test(t)){ c=+t<1500?'r':+t<5000?'a':''; }
  else if((w=t.match(/^[PM]?(\d+)(?:\/(\d+))?SM$/))){ const sm=w[2]?+w[1]/+w[2]:+w[1]; c=sm<1?'r':sm<3?'a':''; }
  else if((w=t.match(/^(BKN|OVC)(\d{3})(CB|TCU)?$/))){ const h=+w[2]; c=w[3]||h<=4?'r':h<=9?'a':''; }
  else if(/^VV\d{3}$/.test(t)||/(CB|TCU)$/.test(t)) c='r';
  else if(/^(FM\d{6}|BECMG|TEMPO|PROB\d{2})$/.test(t)) c='k';
  else if(wxPhen(t)) c=wxPhen(t).bad?'r':'a';
  return c?`<span class="m-${c}">${esc(t)}</span>`:esc(t); }
// TAF printed one change group per line, the way dispatch and Navigraph show it
function wxTafHtml(raw){ return String(raw||'').trim().replace(/\s+(?=(FM\d{6}|BECMG|TEMPO|PROB\d{2})\b)/g,'\n').split('\n')
  .map((l,i)=>(i?'  ':'')+l.trim().split(/\s+/).map(wxTok).join(' ')).join('\n'); }
function wxComp(rwHdg,w){ if(!w||w.vrb||w.dir==null) return null; const d=(w.dir-rwHdg)*Math.PI/180;
  return {hw:Math.round(w.kt*Math.cos(d)), xw:Math.round(w.kt*Math.sin(d)), gx:w.gust?Math.round(w.gust*Math.sin(d)):null}; }
function wxStations(){ const P=plan||{}, S=state||{}, out=[];
  const add=(icao,role)=>{ icao=(icao||'').toUpperCase(); if(/^[A-Z0-9]{4}$/.test(icao)&&!out.some(x=>x.icao===icao)) out.push({icao,role}); };
  add(P.departureIcao||S.departureIcao,'DEP'); add(P.arrivalIcao||S.arrivalIcao,'ARR'); add(P.alternateIcao||S.alternateIcao,'ALTN');
  let rec=[]; try{ rec=JSON.parse(localStorage.getItem(WX_RECENT)||'[]'); }catch{}
  rec.forEach(r=>add(r,'')); return out.slice(0,8); }
function wxRemember(icao){ try{ const r=JSON.parse(localStorage.getItem(WX_RECENT)||'[]').filter(x=>x!==icao); r.unshift(icao); localStorage.setItem(WX_RECENT,JSON.stringify(r.slice(0,6))); }catch{} }
function wxInPoly(lat,lon,ring){ let ins=false; for(let i=0,j=ring.length-1;i<ring.length;j=i++){ const [yi,xi]=ring[i],[yj,xj]=ring[j];
  if(((yi>lat)!==(yj>lat))&&(lon<(xj-xi)*(lat-yi)/(yj-yi)+xi)) ins=!ins; } return ins; }

// ── app ──
// Laid out like the iPad Weather app for one airport at a time: a photograph of the place with
// the conditions in a sentence and the temperature large, the stations (DEP / ARR / ALTN / any
// ICAO) under it, then widgets: ATIS, the wind dial over the runways, visibility, temperature,
// QNH, cloud, the runway wind table, the TAF timeline, hazards and the raw reports.
function renderAtis(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  const st=wxStations(); if(!wx.sel) wx.sel=(st.find(s=>s.role==='ARR')||st[0]||{}).icao||null;
  body.innerHTML=`<div class="fx"><div class="fx-scroll" id="wxScroll">
    <header class="hero" id="wxHero"><div class="hero-tx"><span class="brand" id="wxRole"></span><h1 id="wxName">Weather</h1><p id="wxLine"></p></div>
      <div class="wx-temp" id="wxTemp"></div>${heroTools([['wxRf','Refresh',IC.refresh]])}<a class="hero-cr" target="_blank" rel="noopener"></a></header>
    <div class="fx-pad wx-bar"><nav class="sg wx-tabs" id="wxTabs" aria-label="Stations"></nav>
      <form class="wx-find" id="wxFind" autocomplete="off" role="search"><svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/></svg>
        <input id="wxQ" maxlength="4" placeholder="Any ICAO" spellcheck="false" autocapitalize="characters" aria-label="Search an airport by ICAO"></form></div>
    <div class="fx-pad"><div class="wx-alert hidden" id="wxAlert" role="alert"></div></div>
    <div id="wxMain" class="fx-pad wx-main"></div></div></div>`;
  const tabs=()=>{ $('#wxTabs').innerHTML=wxStations().map(s=>`<button class="${s.icao===wx.sel?'on':''}" data-icao="${s.icao}">${wx.data[s.icao]?.m?.cat?`<i class="wx-dot" style="--c:${WX_CAT[wx.data[s.icao].m.cat]}"></i>`:''}${s.role?`<small>${({DEP:'Dep',ARR:'Arr',ALTN:'Altn'})[s.role]||s.role}</small> `:''}${s.icao}</button>`).join('');
    $$('#wxTabs [data-icao]').forEach(b=>b.onclick=()=>go(b.dataset.icao)); };
  const go=icao=>{ wx.sel=icao; tabs(); paint(); load(icao); };
  $('#wxFind').onsubmit=e=>{ e.preventDefault(); const v=$('#wxQ').value.trim().toUpperCase(); if(/^[A-Z0-9]{4}$/.test(v)){ wxRemember(v); $('#wxQ').value=''; $('#wxQ').blur(); go(v); } };
  $('#wxRf').onclick=()=>{ if(wx.sel) load(wx.sel,true); };
  async function load(icao,force){
    const cur=wx.data[icao]; if(cur&&!force&&Date.now()-cur.at<110000){ paint(); return; }
    $('#wxRf').classList.add('spin');
    try{ const d=await getJSON('/efb/api/wx/station?icao='+icao);
      if(!d||d.error||!d.icao) throw new Error('no data');   // a host-side failure answers {error}, not a station
      wx.data[icao]={d,at:Date.now(),m:wxMetar(d.metar),t:wxTaf(d.taf)};
      checkAtis(icao,d); }
    catch{ if(!wx.data[icao]) wx.data[icao]={err:true,at:Date.now()}; }
    if(!wx.sig||Date.now()-wx.sigAt>600000){ try{ wx.sig=await getJSON('/efb/api/wx/sigmets'); wx.sigAt=Date.now(); }catch{} }
    $('#wxRf')?.classList.remove('spin');
    if(activeApp==='atis'){ tabs(); if(wx.sel===icao) paint(); }
  }
  function checkAtis(icao,d){ (d.atis||[]).forEach(a=>{ const k=icao+'|'+a.callsign, prev=wx.atisSeen[k];
      if(prev&&a.letter&&prev!==a.letter) showAlert(`${icao} information ${a.letter} is current`, `${a.callsign} changed from ${prev} to ${a.letter}. Tell ATC you have information ${a.letter}.`);
      if(a.letter) wx.atisSeen[k]=a.letter; }); }
  function showAlert(t,s){ const el=$('#wxAlert'); if(!el) return; el.innerHTML=`<div><b>${esc(t)}</b><span>${esc(s)}</span></div><button class="tb" aria-label="Dismiss"><svg viewBox="0 0 24 24"><path d="M7 7l10 10M17 7 7 17"/></svg></button>`;
    el.classList.remove('hidden'); el.querySelector('button').onclick=()=>el.classList.add('hidden'); }
  function paint(){ const el=$('#wxMain'); if(!el) return;
    const role=wxStations().find(s=>s.icao===wx.sel)?.role||'', x=wx.data[wx.sel];
    $('#wxRole').textContent=wx.sel?`${wx.sel}${role?', '+({DEP:'departure',ARR:'arrival',ALTN:'alternate'})[role]:''}`:'';
    if(!wx.sel){ $('#wxName').textContent='Weather'; $('#wxLine').textContent='Search any ICAO, or load a flight plan.';
      el.innerHTML=`<div class="w empty"><b>No airport selected</b>Load an OFP for departure, arrival and alternate, or search any ICAO above.</div>`; return; }
    if(!x||(!x.d&&!x.err)){ el.innerHTML=`<div class="w empty">Getting the weather for ${esc(wx.sel)}…</div>`; return; }
    if(x.err&&!x.d){ $('#wxName').textContent=wx.sel; $('#wxLine').textContent='';
      el.innerHTML=`<div class="w empty"><b>Weather unavailable</b>Couldn't reach the weather service for ${esc(wx.sel)}. Try refresh.</div>`; return; }
    const d=x.d, m=x.m, name=d.airport?.name||'';
    $('#wxName').textContent=phPlace(name)||d.icao;
    $('#wxLine').textContent=wxSentence(m);
    $('#wxTemp').innerHTML=m&&m.temp!=null?`<b>${m.temp}°</b>${m.cat?`<span class="cat ${m.cat}">${m.cat}</span>`:''}`:'';
    heroPhoto($('#wxHero'),name||d.icao);
    el.innerHTML=wxView(x);
    $$('#wxMain [data-atab]').forEach(b=>b.onclick=()=>{ wx.atisTab[wx.sel]=+b.dataset.atab; paint(); });
    $$('#wxMain [data-copy]').forEach(b=>b.onclick=async()=>{ if(await copyText(b.dataset.copy)){ b.textContent='Copied'; setTimeout(()=>{ if(b.isConnected) b.textContent='Copy'; },1400); } });
  }
  tabs(); paint(); if(wx.sel) load(wx.sel);
  // background refresh of every listed station's category dots, then the selected one every 2 min
  wxStations().filter(s=>s.icao!==wx.sel&&!wx.data[s.icao]).forEach((s,k)=>setTimeout(()=>load(s.icao),600*(k+1)));
  clearInterval(wx.timer); wx.timer=setInterval(()=>{ if(activeApp==='atis'&&wx.sel) load(wx.sel,true); },120000);
  appCleanup=()=>{ clearInterval(wx.timer); wx.timer=null; };
}
// the conditions in one plain sentence for the header
function wxSentence(m){
  if(!m) return 'No current METAR';
  const w=m.wind, low=m.clouds.filter(c=>c.ft!=null)[0];
  const sky=m.cavok?'CAVOK':m.ceiling!=null?`${WX_COV[m.clouds.find(c=>c.ft===m.ceiling)?.cov]||'Ceiling'} ${fmtN(m.ceiling)} ft`:low?`${WX_COV[low.cov]} ${fmtN(low.ft)} ft`:'Clear';
  const wx=m.wx.length?m.wx.map(p=>p.text).join(', '):'';
  const wind=!w?'':w.kt===0?'wind calm':`wind ${w.vrb?'variable':String(w.dir).padStart(3,'0')+'°'} at ${w.kt} kt${w.gust?`, gusting ${w.gust}`:''}`;
  return [sky+(wx?', '+wx:''),wind].filter(Boolean).join(', ').replace(/^./,c=>c.toUpperCase());
}

function wxView(x){
  const d=x.d, m=x.m, t=x.t, apt=d.airport, P=plan||{};
  const role=wxStations().find(s=>s.icao===wx.sel)?.role||'';
  // ── ATIS ──
  const at=d.atis||[], ai=Math.min(wx.atisTab[wx.sel]||0,Math.max(0,at.length-1)), a=at[ai];
  const atis=`<section class="w w2 h2 wx-atis">
    <h4>ATIS${at.length>1?`<span class="r sg sm">${at.map((s,k)=>`<button data-atab="${k}" class="${k===ai?'on':''}">${esc(s.callsign.replace(d.icao+'_','').replace(/_?ATIS$/,'')||'ATIS')}</button>`).join('')}</span>`:''}</h4>
    ${a?`<div class="wx-atis-h"><b class="wx-letter">${esc(a.letter||'—')}</b><div><span class="wx-net ${a.network.toLowerCase()}">${esc(a.network)}</span><b>${esc(a.callsign)}</b>
        ${a.frequency?`<span class="mono">${a.frequency.toFixed(3)}</span>`:''}</div><button class="bt" data-copy="${esc(a.text||'')}" style="min-height:36px;margin-left:auto">Copy</button></div>
        <pre class="wx-atis-t mono">${esc(a.text||'')}</pre>`
      :d.generatedAtis?`<div class="wx-atis-h"><b class="wx-letter dim">—</b><div><span class="wx-net gen">Built from the METAR</span><b>No ATIS online</b></div></div>
        <pre class="wx-atis-t mono">${esc(d.generatedAtis)}</pre>`
      :`<p>No ATIS online and no METAR to build one from.</p>`}
    ${(d.atc||[]).length?`<div class="wx-atc">${d.atc.map(c=>`<span><i class="wx-net ${c.network.toLowerCase()}">${esc(c.network==='VATSIM'?'V':'I')}</i><b>${esc(c.facility||'ATC')}</b><span class="mono">${c.frequency?c.frequency.toFixed(3):''}</span></span>`).join('')}</div>`:''}
  </section>`;
  if(!m) return `<div class="wg">${atis}<div class="w w2"><h4>METAR</h4><div class="v">None</div><p>No current METAR for ${esc(d.icao)}.</p></div></div>`+wxTafRaw(x,role,P);
  // ── decoded METAR ──
  const w=m.wind, rw=(apt?.runways||[]);
  const comps=rw.map(r=>({r,c:wxComp(r.headingTrueDeg,w)})).filter(x=>x.c);
  const bestHw=comps.length?Math.max(...comps.map(x=>x.c.hw)):null;
  const windTxt=!w?'—':w.kt===0?'Calm':`${w.vrb?'VRB':String(w.dir).padStart(3,'0')+'°'} ${w.kt}${w.gust?`G${w.gust}`:''}<small>kt</small>`;
  const visTxt=m.cavok?'CAVOK':m.vis?(m.vis.plus&&m.vis.m>=9999?'10 km+':m.vis.sm<10&&/SM/.test(m.raw)?`${+m.vis.sm.toFixed(2)}<small>SM</small>`:m.vis.m>=5000?`${(m.vis.m/1000).toFixed(m.vis.m%1000?1:0)}<small>km</small>`:`${num(m.vis.m)}<small>m</small>`):'—';
  const spread=m.temp!=null&&m.dew!=null?m.temp-m.dew:null;
  const obsOld=m.obs&&Date.now()-m.obs>5400000;
  const tiles=`
    <section class="w h2 wx-wind"><h4>Wind</h4><div class="v">${windTxt}</div>${w&&w.from!=null?`<p>Varying ${w.from}° to ${w.to}°</p>`:''}${wxDial(rw,w)}</section>
    <section class="w"><h4>Visibility</h4><div class="v ${m.vis&&m.vis.sm<3?'tone-bad':m.vis&&m.vis.sm<5?'tone-warn':''}">${visTxt}</div>
      ${m.rvr.length?`<p>${m.rvr.map(r=>`RVR ${r.rwy} ${r.pfx==='P'?'>':r.pfx==='M'?'<':''}${r.min}${r.max?'–'+r.max:''}${r.ft?' ft':' m'}${r.trend==='U'?', rising':r.trend==='D'?', falling':''}`).join('; ')}</p>`:`<p>${m.ceiling!=null?`Ceiling ${num(m.ceiling)} ft`:'No ceiling'}</p>`}</section>
    <section class="w"><h4>Temperature</h4><div class="v">${m.temp!=null?m.temp+'°':'—'}</div><p class="${spread!=null&&spread<=2?'tone-warn':''}">Dew point ${m.dew??'—'}°${spread!=null&&spread<=2?', fog or low cloud likely':''}${m.temp!=null&&m.temp<=3&&m.temp>=-15&&(m.wx.length||m.clouds.some(c=>c.cov!=='FEW'))?', icing conditions':''}</p></section>
    <section class="w"><h4>QNH</h4><div class="v">${m.qnh?`${m.qnh}<small>hPa</small>`:'—'}</div><p>${m.inhg?m.inhg.toFixed(2)+' inHg':''}</p></section>
    <section class="w wx-cloud"><h4>Cloud</h4>${wxClouds(m)}</section>
    <section class="w w2"><h4>Observed<span class="r ${obsOld?'tone-warn':''}">${m.obs?wxAge(m.obs):''}</span></h4><div class="v">${m.obs?wxZ(m.obs):'—'}</div>
      <p>${[m.speci?'Special report (SPECI)':'',m.auto?'Automatic station':'',obsOld?'More than 90 minutes old':''].filter(Boolean).join(', ')||'Routine report'}</p>
      ${m.wx.length||m.trend?`<div class="wx-wxl">${m.wx.map(p=>`<span class="${p.bad?'bad':''}"><b class="mono">${esc(p.code)}</b>${esc(p.text)}</span>`).join('')}${m.trend?`<span class="trend"><b>Trend</b>${esc(m.trend)}</span>`:''}</div>`:''}</section>`;
  const rwys=comps.length?`<div class="gl-h">Runways and wind</div><div class="gl wx-rw">
      ${comps.sort((p,q)=>q.c.hw-p.c.hw).map(({r,c})=>`<div class="row${c.hw===bestHw&&c.hw>0?' best':''}"><span class="k"><b>${esc(r.ident)}</b><small>${String(r.headingTrueDeg).padStart(3,'0')}° true${r.lengthM?', '+num(r.lengthM)+' m':''}</small></span>
        ${c.hw===bestHw&&c.hw>0?'<span class="wx-into">Into wind</span>':''}
        <span class="val ${c.hw<-10?'tone-bad':c.hw<0?'tone-warn':''}">${c.hw>=0?'Head':'Tail'} ${Math.abs(c.hw)}<small>kt</small></span>
        <span class="val ${Math.abs(c.gx??c.xw)>=25?'tone-bad':Math.abs(c.gx??c.xw)>=15?'tone-warn':''}">Cross ${Math.abs(c.xw)}${c.gx!=null&&Math.abs(c.gx)!==Math.abs(c.xw)?' G'+Math.abs(c.gx):''}<small>${c.xw>0?'from the right':c.xw<0?'from the left':'kt'}</small></span></div>`).join('')}</div>`
    :w&&(w.vrb||w.kt===0)?`<p class="gl-f">Wind ${w.kt===0?'calm':'variable'}, so no runway is favoured.</p>`:'';
  return `<div class="wg">${atis}${tiles}</div>${rwys}${wxTafRaw(x,role,P)}`;
}
function wxTafRaw(x,role,P){
  const d=x.d, t=x.t, apt=d.airport;
  let taf='';
  if(t&&t.groups.length){
    const etaD=role==='ARR'&&P.scheduledArrivalUtc?new Date(P.scheduledArrivalUtc):role==='DEP'&&P.scheduledDepartureUtc?new Date(P.scheduledDepartureUtc):null;
    const eta=etaD&&etaD>=t.from&&etaD<t.to?etaD:null, fc=eta&&wxTafAt(t,eta);
    const condTxt=c=>[c.wind?(c.wind.vrb?'VRB':String(c.wind.dir).padStart(3,'0'))+c.wind.kt+(c.wind.gust?'G'+c.wind.gust:'')+'KT':'',c.cavok?'CAVOK':c.vis?(c.vis.m>=9999?'9999':String(Math.round(c.vis.m))):'',...c.wx.map(p=>p.code),...c.clouds.map(l=>l.cov+String(Math.round((l.ft||0)/100)).padStart(3,'0')+l.type),c.vv!=null?'VV'+String(c.vv/100).padStart(3,'0'):''].filter(Boolean).join(' ');
    taf=`<div class="gl-h">Forecast<span class="gl-hr">Valid ${wxZ(t.from)} to ${wxZ(t.to)}</span></div><div class="w wx-taf">
      ${wxTimeline(t,eta,role)}
      ${fc?`<div class="wx-eta"><b>${role==='ARR'?'At your arrival':'At your departure'}, ${wxZ(eta)}</b><span class="cat ${fc.cat||''}">${fc.cat||'—'}</span><span class="mono">${esc(condTxt(fc.prev))}</span>
        ${fc.temp.map(x=>`<span class="wx-tmp"><em>${x.type}${x.prob?' '+x.prob+'%':''}</em><span class="cat ${x.cat||''}">${x.cat||'—'}</span><span class="mono">${esc(x.raw)}</span></span>`).join('')}</div>`:''}
    </div>`;
  }
  const sig=(wx.sig||[]).filter(s=>apt&&s.rings&&s.rings.some(r=>wxInPoly(apt.latitude,apt.longitude,r)));
  const haz=sig.length?`<div class="gl-h">Hazards over ${esc(d.icao)}</div><div class="gl">
    ${sig.map(s=>`<div class="row"><span class="wx-hz" style="--c:${/TS|CONVECTIVE/i.test(s.hazard)?'#e5484d':/ICE|ICING/i.test(s.hazard)?'#0a9cc4':/TURB/i.test(s.hazard)?'#e08a00':'#a23ac7'}">${esc(s.kind||'SIGMET')}</span>
      <span class="k">${esc(s.hazard||'')}</span><span class="val">${s.baseFt!=null||s.topFt!=null?`${s.baseFt?'FL'+Math.round(s.baseFt/100):'Surface'} to ${s.topFt?'FL'+Math.round(s.topFt/100):'—'}`:''}<small>${s.validTo?'until '+wxZ(new Date(typeof s.validTo==='number'?s.validTo*1000:s.validTo)):''}</small></span></div>`).join('')}</div>`:'';
  const raw=`<div class="gl-h">Reports<button class="bt gl-hr" data-copy="${esc((d.metar||'')+'\n'+(d.taf||''))}" style="min-height:32px;padding:0 14px;font-size:14px">Copy</button></div>
    <div class="gl wx-raw"><div class="metar-raw"><small>METAR</small><pre class="mono">${d.metar?d.metar.trim().split(/\s+/).map(wxTok).join(' '):'No METAR available.'}</pre></div>
    <div class="metar-raw"><small>TAF</small><pre class="mono">${d.taf?wxTafHtml(d.taf):'No TAF available.'}</pre></div></div>`;
  return taf+haz+raw;
}

// wind dial: runways (true) under the wind arrow, like the flyPad / Navigraph wind widget
function wxDial(rw,w){
  const S=150, c=S/2, R=58, strips={};
  rw.forEach(r=>{ const k=r.strip||r.ident; if(!strips[k]) strips[k]=r; });
  let s=`<circle cx="${c}" cy="${c}" r="${R}" class="wd-ring"/>`;
  for(let a=0;a<360;a+=10){ const big=a%30===0, r1=R-(big?7:4), x=Math.sin(a*Math.PI/180), y=-Math.cos(a*Math.PI/180);
    s+=`<line x1="${(c+x*r1).toFixed(1)}" y1="${(c+y*r1).toFixed(1)}" x2="${(c+x*R).toFixed(1)}" y2="${(c+y*R).toFixed(1)}" class="wd-tk${big?' b':''}"/>`; }
  [['N',0],['E',90],['S',180],['W',270]].forEach(([l,a])=>{ const x=Math.sin(a*Math.PI/180), y=-Math.cos(a*Math.PI/180);
    s+=`<text x="${(c+x*(R+10)).toFixed(1)}" y="${(c+y*(R+10)+3.5).toFixed(1)}" class="wd-l">${l}</text>`; });
  Object.values(strips).forEach(r=>{ s+=`<g transform="rotate(${r.headingTrueDeg} ${c} ${c})"><rect x="${c-5}" y="${c-R+12}" width="10" height="${2*R-24}" rx="1.5" class="wd-rw"/>
    <line x1="${c}" y1="${c-R+16}" x2="${c}" y2="${c+R-16}" class="wd-cl"/></g>`; });
  if(w&&!w.vrb&&w.dir!=null&&w.kt>0){ const a=w.dir;
    s+=`<g transform="rotate(${a} ${c} ${c})"><line x1="${c}" y1="${c-R-4}" x2="${c}" y2="${c-16}" class="wd-ar"/><path d="M${c-7} ${c-26}L${c} ${c-12}L${c+7} ${c-26}Z" class="wd-ah"/></g>`;
    if(w.from!=null){ const f=w.from*Math.PI/180, t2=w.to*Math.PI/180, rr=R+2, large=((w.to-w.from+360)%360)>180?1:0;
      s+=`<path d="M${(c+Math.sin(f)*rr).toFixed(1)} ${(c-Math.cos(f)*rr).toFixed(1)}A${rr} ${rr} 0 ${large} 1 ${(c+Math.sin(t2)*rr).toFixed(1)} ${(c-Math.cos(t2)*rr).toFixed(1)}" class="wd-var"/>`; }
  } else s+=`<text x="${c}" y="${c+4}" class="wd-calm">${w&&w.vrb?'VRB':'CALM'}</text>`;
  return `<svg class="wx-dial" viewBox="0 0 ${S} ${S}" role="img" aria-label="Wind and runways">${s}</svg>`;
}
// cloud layers stacked by height; ceiling layer marked, CB/TCU in red
function wxClouds(m){
  if(m.cavok) return `<div class="v">CAVOK</div><p>No cloud below 5,000 ft, no CB, visibility 10 km or more.</p>`;
  const L=m.clouds.filter(c=>c.ft!=null); if(!L.length&&m.vv==null) return `<div class="v">${/NSC|NCD|SKC|CLR/.test(m.raw)?'None':'—'}</div><p>${/NSC/.test(m.raw)?'No significant cloud':/NCD/.test(m.raw)?'No cloud detected (automatic)':/SKC|CLR/.test(m.raw)?'Sky clear':'Not reported'}</p>`;
  const top=Math.max(6000,...L.map(c=>c.ft+1500)), W=170, H=110, y=ft=>H-6-(ft/top)*(H-16), wpc={FEW:.25,SCT:.5,BKN:.78,OVC:1};
  let s=`<line x1="0" y1="${H-6}" x2="${W}" y2="${H-6}" class="cl-gnd"/>`;
  for(let ft=0;ft<=top;ft+=top>12000?5000:2000) s+=`<text x="2" y="${(y(ft)-2).toFixed(1)}" class="cl-ax">${ft?num(ft/1000)+'k':''}</text>`;
  const ceil=m.ceiling;
  L.forEach(c=>{ const yy=y(c.ft), w=(W-80)*wpc[c.cov], isC=c.ft===ceil&&(c.cov==='BKN'||c.cov==='OVC');
    s+=`<rect x="26" y="${(yy-5).toFixed(1)}" width="${w.toFixed(1)}" height="10" rx="5" class="cl-l ${c.type?'cb':''} ${isC?'ceil':''}"/>
      <text x="${(32+w).toFixed(1)}" y="${(yy+4).toFixed(1)}" class="cl-t">${c.cov}${String(c.ft/100).padStart(3,'0')}${c.type}</text>`; });
  if(m.vv!=null) s+=`<rect x="26" y="${y(m.vv).toFixed(1)}" width="${W-30}" height="${(H-6-y(m.vv)).toFixed(1)}" class="cl-vv"/><text x="30" y="${(y(m.vv)+12).toFixed(1)}" class="cl-t">VV${String(m.vv/100).padStart(3,'0')} sky obscured</text>`;
  return `<svg class="wx-cl" viewBox="0 0 ${W} ${H}" role="img" aria-label="Cloud layers">${s}</svg>`;
}
// TAF timeline: one cell per hour, prevailing on top, TEMPO/PROB below, ETA marker
function wxTimeline(t,eta,role){
  const hrs=Math.min(36,Math.max(1,Math.round((t.to-t.from)/36e5))), W=640, cw=W/hrs, rows=[];
  for(let h=0;h<hrs;h++){ const at=new Date(+t.from+h*36e5+18e5); rows.push(wxTafAt(t,at)); }
  let s='';
  rows.forEach((r,h)=>{ const x=h*cw;
    s+=`<rect x="${(x+.5).toFixed(1)}" y="18" width="${(cw-1).toFixed(1)}" height="22" rx="3" fill="${r&&r.cat?WX_CAT[r.cat]:'var(--s-fill)'}"/>`;
    const worst=r&&r.temp.length?r.temp.reduce((a,b)=>wxRank[b.cat]>wxRank[a.cat]?b:a):null;
    if(worst&&worst.cat) s+=`<rect x="${(x+.5).toFixed(1)}" y="44" width="${(cw-1).toFixed(1)}" height="12" rx="3" fill="${WX_CAT[worst.cat]}" opacity="${worst.type.includes('PROB')?.45:.8}"/>`;
    const hh=new Date(+t.from+h*36e5).getUTCHours();
    if(hh%3===0) s+=`<text x="${(x+2).toFixed(1)}" y="12" class="tl-t">${String(hh).padStart(2,'0')}Z</text>`;
    if(hh===0&&h>0) s+=`<line x1="${x.toFixed(1)}" y1="2" x2="${x.toFixed(1)}" y2="58" class="tl-d"/>`; });
  if(eta){ const ex=((eta-t.from)/36e5)*cw; s+=`<line x1="${ex.toFixed(1)}" y1="14" x2="${ex.toFixed(1)}" y2="60" class="tl-eta"/><text x="${(ex+4).toFixed(1)}" y="72" class="tl-et">${role==='ARR'?'Arrival':'Departure'} ${wxZ(eta)}</text>`; }
  return `<div class="wx-tl"><svg viewBox="0 0 ${W} ${eta?78:62}" role="img" aria-label="TAF timeline">${s}</svg>
    <div class="wx-leg"><span>Top row prevailing, bottom row TEMPO or PROB</span>${Object.entries(WX_CAT).map(([k,v])=>`<span><i style="background:${v}"></i>${k}</span>`).join('')}</div></div>`;
}

// ── ACARS APP (datalink terminal on the Hoppie network) ──
// Laid out like an airline ACARS / ATC datalink page (Fenix and flyPad's ATC page, the A320
// DCDU): a message list with folders, the selected message on a DCDU-style screen with its
// WILCO / UNABLE / STANDBY keys, and a composer for TELEX (company dispatch on VAOPS, any
// station), CLEARANCE (pre-departure clearance request), INFO (METAR / TAF / VATSIM ATIS) and
// CPDLC (logon, requests, logoff). It all runs on the host under the pilot's own Hoppie logon;
// the inbox is peeked, so an in-sim ACARS unit on the same callsign still gets every message.
// The automatic company messages the tracker sent (loadsheet, reminders…) are the OPS LOG.
let acars={d:null, folder:'all', sel:null, tab:'telex', pane:'list', busy:false, err:'', note:'', lastCp:'',
  f:{to:'VAOPS', text:'', pdcStation:'', stand:'', atis:'', dest:'', type:'', kind:'METAR', icao:'',
     station:'', rq:'LEVEL', lvlDir:'CLIMB', val:'', why:'', free:''}};
const AC_FOLDERS=[['all','All'],['in','Inbox'],['out','Sent'],['cpdlc','CPDLC'],['co','Company'],['info','Weather'],['ops','Ops log']];
const AC_ICO={
  ref:`<svg viewBox="0 0 24 24"><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 5v6h-6"/></svg>`,
  in:`<svg viewBox="0 0 24 24"><path d="M12 4v12M6 10l6 6 6-6M5 20h14"/></svg>`,
  out:`<svg viewBox="0 0 24 24"><path d="M12 20V8M6 14l6-6 6 6M5 4h14"/></svg>`,
  copy:`<svg viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>`,
  back:`<svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6"/></svg>`,
};
const acMsgs=()=>(acars.d&&acars.d.messages)||[];
function acFiltered(){
  const f=acars.folder, M=acMsgs().slice().reverse();
  if(f==='ops') return ((acars.d&&acars.d.ops)||[]).slice().reverse().map((o,i)=>({...o,id:'ops'+i,dir:'ops',station:'VAOPS',kind:o.category,text:o.message,utc:o.utc,read:true,status:o.success?'sent':'failed'}));
  return M.filter(m=>f==='all'||(f==='in'&&m.dir==='in')||(f==='out'&&m.dir==='out')||(f==='cpdlc'&&m.type==='cpdlc')
    ||(f==='co'&&(m.station==='VAOPS'||m.kind==='COMPANY'))||(f==='info'&&m.type==='info'));
}
function acFind(id){ if(!id) return null; return acMsgs().find(m=>m.id===id)||(String(id).startsWith('ops')?acFiltered().find(m=>m.id===id):null); }

async function acFetch(){
  try{ const a=await getJSON('/efb/api/acars'); if(a&&Array.isArray(a.messages)){ acars.d=a; renderBadges(); if(activeApp==='acars'){ acRefresh(); acCompDraw(); } } }catch{}
}
function renderAcars(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  const f=acars.f, d=acars.d||{};
  if(!f.pdcStation) f.pdcStation=d.departure||'';
  if(!f.dest) f.dest=d.arrival||''; if(!f.type) f.type=d.aircraftType||'';
  if(!f.icao) f.icao=d.arrival||d.departure||'';
  body.innerHTML=`<div class="ac" id="ac">
    <div class="ac-top" id="acTop"></div>
    <div id="acBanner"></div>
    <div class="ac-mtabs" role="tablist"><button data-p="list">Messages</button><button data-p="new">New message</button></div>
    <div class="ac-g">
      <aside class="ac-list">
        <div class="ac-folders" role="tablist" aria-label="Folders">${AC_FOLDERS.map(([k,l])=>`<button data-f="${k}" role="tab">${l}<i></i></button>`).join('')}</div>
        <div class="ac-lh"><span id="acCount"></span><button class="ac-link" id="acAllRead">Mark all as read</button></div>
        <div class="ac-rows" id="acRows"></div>
      </aside>
      <section class="ac-view" id="acView" aria-live="polite"></section>
      <section class="ac-comp" id="acComp"></section>
    </div>
  </div>`;
  $$('#ac .ac-folders button').forEach(b=>b.onclick=()=>{ acars.folder=b.dataset.f; acars.sel=null; acListDraw(); acViewDraw(); });
  $$('#ac .ac-mtabs button').forEach(b=>b.onclick=()=>{ acars.pane=b.dataset.p; acPane(); });
  $('#acAllRead').onclick=()=>{ const ids=acMsgs().filter(m=>m.dir==='in'&&!m.read).map(m=>m.id); if(ids.length) acRead(ids); };
  acTopDraw(); acListDraw(); acViewDraw(); acCompDraw(); acPane();
  // a fresh look at the inbox when the app opens (the host peeks every 45–75 s anyway);
  // fetch the state first: the callsign may have arrived with a plan since it was last pushed
  if(d.hasLogon&&d.callsign&&(!d.lastPollUtc||Date.now()-new Date(d.lastPollUtc)>20e3)) acPoll(true); else acFetch();
  const iv=setInterval(acTopDraw,15000);
  appCleanup=()=>clearInterval(iv);
}
function acPane(){ const r=$('#ac'); if(!r) return; r.dataset.pane=acars.pane;
  $$('#ac .ac-mtabs button').forEach(b=>b.classList.toggle('on',b.dataset.p===(acars.pane==='view'?'list':acars.pane))); }
// a WebSocket "acars" push: everything but the composer's inputs
function acRefresh(){
  if(activeApp!=='acars'||!$('#ac')) return;
  acTopDraw(); acListDraw(); acViewDraw();
  const cp=acCpKey(); if(acars.tab==='cpdlc'&&cp!==acars.lastCp) acCompDraw();
}
const acCpKey=()=>{ const d=acars.d||{}; return [d.cpdlcState,d.cpdlcStation,d.nextStation,d.callsign,d.hasLogon].join('|'); };

function acTopDraw(){
  const el=$('#acTop'); if(!el) return; const d=acars.d||{};
  const cp=d.cpdlcState||'off';
  const age=d.lastPollUtc?Math.round((Date.now()-new Date(d.lastPollUtc))/60e3):null;
  el.innerHTML=`<div class="ac-id"><b>ACARS</b><span>Datalink on the Hoppie network</span></div>
    <span class="ac-chip ${d.hasLogon&&d.callsign?(d.lastPollOk||!d.lastPollUtc?'on':'warn'):'off'}" title="Your ATC callsign on the datalink"><i></i>${esc(d.callsign||'No callsign')}</span>
    <span class="ac-chip cp-${cp}" title="CPDLC"><b>CPDLC</b>${cp==='connected'?esc(d.cpdlcStation)+' <small>connected</small>':cp==='pending'?esc(d.cpdlcStation)+' <small>logon sent</small>':'<small>not logged on</small>'}</span>
    <span class="ac-sp"></span>
    <span class="ac-upd">${d.lastPollUtc?`Checked ${fmtT(d.lastPollUtc)}${age>1?`, ${age} min ago`:''}`:'Not checked yet'}</span>
    <button class="ac-rf" id="acRf" title="Check for new messages" aria-label="Check for new messages" ${d.hasLogon&&d.callsign?'':'disabled'}>${AC_ICO.ref}</button>`;
  $('#acRf').onclick=()=>acPoll(false);
  const bn=$('#acBanner');
  if(bn) bn.innerHTML=!d.hasLogon?`<div class="ac-ban"><b>Datalink is offline</b>Add your Hoppie logon code in the desktop app (Settings › Hoppies ACARS). It's free from hoppie.nl/acars. The ops log below still shows the automatic company messages.</div>`
    :!d.callsign?`<div class="ac-ban"><b>No callsign</b>Book a flight or load a SimBrief plan. ACARS uses its ATC callsign.</div>`
    :d.lastError?`<div class="ac-ban bad"><b>Last check failed</b>${esc(d.lastError)}</div>`:'';
}
async function acPoll(quiet){
  const b=$('#acRf'); if(b) b.classList.add('spin');
  try{ const a=await postJSON('/efb/api/acars/poll',{}); if(a&&Array.isArray(a.messages)){ acars.d=a; renderBadges(); } }catch{}
  if(b&&b.isConnected) b.classList.remove('spin');
  if(!quiet) acRefresh(); else { acTopDraw(); acListDraw(); acViewDraw(); }
}
function acRead(ids){
  for(const m of acMsgs()) if(ids.includes(m.id)) m.read=true;
  if(acars.d) acars.d.unread=acMsgs().filter(m=>m.dir==='in'&&!m.read).length;
  renderBadges(); acListDraw();
  postJSON('/efb/api/acars/read',{ids}).catch(()=>{});
}

function acListDraw(){
  const el=$('#acRows'); if(!el) return;
  const L=acFiltered(), M=acMsgs();
  const cnt={all:M.filter(m=>m.dir==='in'&&!m.read).length,in:M.filter(m=>m.dir==='in'&&!m.read).length,cpdlc:M.filter(m=>m.type==='cpdlc'&&m.open).length,
    co:M.filter(m=>(m.station==='VAOPS'||m.kind==='COMPANY')&&m.dir==='in'&&!m.read).length};
  $$('#ac .ac-folders button').forEach(b=>{ b.classList.toggle('on',b.dataset.f===acars.folder); b.setAttribute('aria-selected',b.dataset.f===acars.folder);
    const n=cnt[b.dataset.f]||0; b.querySelector('i').textContent=n||''; b.classList.toggle('has',n>0); b.classList.toggle('open',b.dataset.f==='cpdlc'&&n>0); });
  $('#acCount').textContent=`${L.length} message${L.length===1?'':'s'}`;
  $('#acAllRead').classList.toggle('hidden',!cnt.in);
  el.innerHTML=L.length?L.map(m=>`<button class="ac-row${m.id===acars.sel?' on':''}${m.dir==='in'&&!m.read?' unread':''}${m.open?' open':''}${m.status==='failed'?' fail':''}" data-id="${esc(m.id)}">
      <span class="ac-dir ${m.dir}">${m.dir==='out'?AC_ICO.out:AC_ICO.in}</span>
      <span class="ac-rt"><span class="ac-r1"><b class="mono">${m.dir==='out'?'TO ':''}${esc(m.station||'—')}</b><em class="k-${esc((m.kind||'').split(' ')[0].toLowerCase())}">${esc(m.kind||m.type||'')}</em>
        <time class="mono">${fmtT(m.utc)}</time></span>
        <span class="ac-r2 mono">${esc((m.text||'').replace(/\n/g,' '))}</span>
        ${m.open?'<span class="ac-st am">Response required</span>':m.response?`<span class="ac-st ${/UNABLE|NEGATIVE/.test(m.response)?'rd':'gr'}">${m.dir==='in'?'':'ATC: '}${esc(m.response.split('\n')[0])}</span>`:m.status==='failed'?'<span class="ac-st rd">Not sent</span>':''}</span></button>`).join('')
    :`<div class="ac-none">${acars.folder==='ops'?'No automatic company messages yet. They start when a flight is booked and Hoppies messages are on in the desktop app.':'Nothing here yet.'}</div>`;
  $$('#acRows .ac-row').forEach(r=>r.onclick=()=>acSelect(r.dataset.id));
}
function acSelect(id){
  acars.sel=id; const m=acFind(id);
  if(m&&m.dir==='in'&&!m.read&&!String(id).startsWith('ops')) acRead([id]);
  acars.pane='view'; acPane(); acListDraw(); acViewDraw();
}

function acViewDraw(){
  const el=$('#acView'); if(!el) return;
  const m=acFind(acars.sel);
  if(!m){ el.innerHTML=`<div class="dcdu idle"><div class="dc-scr"><div class="dc-idle mono">${acars.d&&acars.d.callsign?`${esc(acars.d.callsign)}<br><small>SELECT A MESSAGE</small>`:'NO ACTIVE DATALINK'}</div></div></div>`; return; }
  const st=m.open?['OPEN','am']:m.response&&m.dir==='in'?[m.response,/UNABLE|NEGATIVE/.test(m.response)?'rd':'gr']:m.status==='failed'?['NOT SENT','rd']:m.dir==='out'?['SENT','gr']:['',''];
  const resp=m.open?(m.responses||[]):[];
  el.innerHTML=`<div class="dcdu">
    <button class="ac-back" id="acBack">${AC_ICO.back}Messages</button>
    <div class="dc-scr">
      <div class="dc-h mono"><span class="cy">${fmtT(m.utc)} ${m.dir==='out'?'TO':'FROM'} ${esc(m.station)}${m.dir==='ops'?' (AUTO)':''}</span><span>${esc(m.kind||m.type||'')}</span><span class="${st[1]}">${esc(st[0])}</span></div>
      <pre class="dc-txt mono">${esc(m.text||'')}</pre>
      ${m.dir==='out'&&m.response?`<div class="dc-resp mono"><span class="cy">ATC RESPONSE</span>\n${esc(m.response)}</div>`:''}
      ${m.error?`<div class="dc-resp mono rd">${esc(m.error)}</div>`:''}
      ${m.min!=null?`<div class="dc-meta mono">MIN ${m.min}${m.mrn!=null?` · MRN ${m.mrn}`:''}${m.ra?` · RA ${esc(m.ra)}`:''}</div>`:''}
    </div>
    <div class="dc-keys">
      ${resp.map(r=>`<button class="dc-k ${r==='WILCO'||r==='ROGER'||r==='AFFIRM'?'g':r==='STANDBY'?'a':'r'}" data-r="${r}">${r}</button>`).join('')}
      <span class="ac-sp"></span>
      ${m.dir==='in'&&m.type==='telex'?'<button class="dc-k s" id="acReply">REPLY</button>':''}
      <button class="dc-k s" id="acCopy">${AC_ICO.copy}COPY</button>
    </div>
    <div class="ac-err hidden" id="acViewErr" role="alert"></div>
  </div>`;
  $('#acBack').onclick=()=>{ acars.pane='list'; acPane(); };
  $('#acCopy').onclick=async()=>{ if(await copyText(m.text||'')) flash($('#acCopy'),'COPIED'); };
  if($('#acReply')) $('#acReply').onclick=()=>{ acars.tab='telex'; acars.f.to=m.station; acars.pane='new'; acPane(); acCompDraw(); const t=$('#acText'); if(t) t.focus(); };
  $$('#acView .dc-k[data-r]').forEach(b=>b.onclick=async()=>{
    $$('#acView .dc-k[data-r]').forEach(x=>x.disabled=true);
    const r=await acSend('/efb/api/acars/cpdlc/respond',{id:m.id,response:b.dataset.r});
    if(!r.ok){ const e=$('#acViewErr'); if(e){ e.textContent=r.error||'Not sent.'; e.classList.remove('hidden'); } $$('#acView .dc-k[data-r]').forEach(x=>x.disabled=false); }
  });
}

async function acSend(path,body){
  acars.busy=true; acBusy();
  let r; try{ r=await postJSON(path,body); }catch{ r={ok:false,error:'Could not reach the desktop app.'}; }
  acars.busy=false;
  if(r&&r.state&&Array.isArray(r.state.messages)){ acars.d=r.state; renderBadges(); }
  acars.err=r&&r.ok?'':((r&&r.error)||'Not sent.');
  if(r&&r.ok&&r.message){ acars.sel=r.message.id; if(acars.folder!=='all'&&!acFiltered().some(x=>x.id===r.message.id)) acars.folder='all'; }
  acTopDraw(); acListDraw(); acViewDraw(); acBusy();
  const e=$('#acErr'); if(e){ e.textContent=acars.err; e.classList.toggle('hidden',!acars.err); }
  return r||{ok:false};
}
function acBusy(){ $$('#acComp .ac-go').forEach(b=>{ b.disabled=acars.busy||b.dataset.dis==='1'; b.classList.toggle('busy',acars.busy); }); }

// ── composer ──
function acCompDraw(){
  const el=$('#acComp'); if(!el) return; const d=acars.d||{}, f=acars.f, off=!d.hasLogon||!d.callsign;
  acars.lastCp=acCpKey();
  const tabs=[['telex','Telex'],['pdc','Clearance'],['info','Info'],['cpdlc','CPDLC']];
  const chip=(v,l)=>v?`<button type="button" class="ac-chipb" data-v="${esc(v)}">${esc(l)}</button>`:'';
  let h=`<div class="ac-tabs" role="tablist">${tabs.map(([k,l])=>`<button role="tab" data-t="${k}" class="${acars.tab===k?'on':''}" aria-selected="${acars.tab===k}">${l}</button>`).join('')}</div>
    <form class="ac-form" id="acForm" autocomplete="off">`;
  if(acars.tab==='telex'){
    h+=`<label class="ac-fl"><span>To</span><input id="acTo" class="mono up" maxlength="10" value="${esc(f.to)}" placeholder="Station"></label>
      <div class="ac-chips" data-for="acTo">${chip('VAOPS','VAOPS (company dispatch)')}${chip(d.departure,'Departure '+(d.departure||''))}${chip(d.arrival,'Arrival '+(d.arrival||''))}</div>
      <label class="ac-fl grow"><span>Message <small id="acCnt">${f.text.length}/500</small></span><textarea id="acText" class="mono up" rows="4" maxlength="500" placeholder="Free text">${esc(f.text)}</textarea></label>
      <p class="ac-hint">${f.to==='VAOPS'?'Goes to AviatesAir dispatch. The AI dispatcher answers company questions here within a minute or two.':'A telex to any Hoppie station: a controller, another pilot\'s callsign or a company station.'}</p>
      <button class="ac-go" data-act="telex">Send telex</button>`;
  } else if(acars.tab==='pdc'){
    const prev=`REQUEST PREDEP CLEARANCE ${d.callsign||'CALLSIGN'} ${(f.type||'TYPE').toUpperCase()} TO ${(f.dest||'DEST').toUpperCase()} AT ${(d.departure||f.pdcStation||'DEP').toUpperCase()}${f.stand?' STAND '+f.stand.toUpperCase():''}${f.atis?' ATIS '+f.atis.toUpperCase():''}`;
    h+=`<div class="ac-grid4">
        <label class="ac-fl"><span>Station</span><input id="acPdcSt" class="mono up" maxlength="10" value="${esc(f.pdcStation)}" placeholder="EGLL"></label>
        <label class="ac-fl"><span>Destination</span><input id="acDest" class="mono up" maxlength="4" value="${esc(f.dest)}" placeholder="LSZH"></label>
        <label class="ac-fl"><span>Aircraft</span><input id="acType" class="mono up" maxlength="4" value="${esc(f.type)}" placeholder="A20N"></label>
        <label class="ac-fl"><span>Stand</span><input id="acStand" class="mono up" maxlength="6" value="${esc(f.stand)}" placeholder="512"></label>
        <label class="ac-fl"><span>ATIS</span><input id="acAtis" class="mono up" maxlength="1" value="${esc(f.atis)}" placeholder="K"></label>
      </div>
      <pre class="ac-prev mono" id="acPrev">${esc(prev)}</pre>
      <p class="ac-hint">For a VATSIM or IVAO delivery controller that offers PDC over Hoppie. Their clearance comes back as a CPDLC message: check it, then WILCO.</p>
      <button class="ac-go" data-act="pdc">Request clearance</button>`;
  } else if(acars.tab==='info'){
    h+=`<div class="ac-seg">${['METAR','TAF','ATIS'].map(k=>`<button type="button" data-k="${k}" class="${f.kind===k?'on':''}">${k}</button>`).join('')}</div>
      <label class="ac-fl"><span>Airport</span><input id="acIcao" class="mono up" maxlength="4" value="${esc(f.icao)}" placeholder="ICAO"></label>
      <div class="ac-chips" data-for="acIcao">${chip(d.departure,'Departure '+(d.departure||''))}${chip(d.arrival,'Arrival '+(d.arrival||''))}${chip(d.alternate,'Alternate '+(d.alternate||''))}</div>
      <p class="ac-hint">${f.kind==='ATIS'?'The VATSIM controller ATIS for the airport, if one is online.':'Straight from the Hoppie server, the way an aircraft ACARS asks for weather.'}</p>
      <button class="ac-go" data-act="info">Request ${f.kind}</button>`;
  } else {
    const cp=d.cpdlcState||'off';
    if(cp==='off'){
      h+=`<div class="ac-cp off"><b>Not logged on</b><span>Log on to the controller's CPDLC station (usually their FIR or airport code, e.g. EDGG or EGTT).</span></div>
        <label class="ac-fl"><span>ATC station</span><input id="acStation" class="mono up" maxlength="10" value="${esc(f.station||d.nextStation||'')}" placeholder="EDGG"></label>
        ${d.nextStation?`<div class="ac-chips" data-for="acStation">${chip(d.nextStation,'Next unit '+d.nextStation)}</div>`:''}
        <button class="ac-go" data-act="logon">Log on</button>`;
    } else if(cp==='pending'){
      h+=`<div class="ac-cp pending"><b>Logon sent to ${esc(d.cpdlcStation)}</b><span>Waiting for the controller to accept. It can take a minute.</span></div>
        <button class="ac-go sec" data-act="logoff">Cancel the logon</button>`;
    } else {
      const rq=f.rq;
      h+=`<div class="ac-cp on"><b>Connected to ${esc(d.cpdlcStation)}</b><span>Current data authority. Requests go to this station.</span>
          <button type="button" class="ac-link" data-act="logoff">Log off</button></div>
        <div class="ac-seg">${[['LEVEL','Level'],['DIRECT','Direct'],['SPEED','Speed'],['HEADING','Heading'],['FREE','Free text']].map(([k,l])=>`<button type="button" data-rq="${k}" class="${rq===k?'on':''}">${l}</button>`).join('')}</div>
        ${rq==='LEVEL'?`<div class="ac-grid2"><div class="ac-seg sm">${['CLIMB','DESCENT'].map(k=>`<button type="button" data-ld="${k}" class="${f.lvlDir===k?'on':''}">${k}</button>`).join('')}</div>
            <label class="ac-fl"><span>Level</span><input id="acVal" class="mono up" maxlength="5" value="${esc(f.val)}" placeholder="FL360"></label></div>`
        :rq==='FREE'?`<label class="ac-fl grow"><span>Free text</span><textarea id="acFree" class="mono up" rows="3" maxlength="200" placeholder="WHEN CAN WE EXPECT HIGHER LEVEL">${esc(f.free)}</textarea></label>`
        :`<label class="ac-fl"><span>${rq==='DIRECT'?'Waypoint':rq==='SPEED'?'Speed (kt or M.78)':'Heading'}</span><input id="acVal" class="mono up" maxlength="6" value="${esc(f.val)}" placeholder="${rq==='DIRECT'?'BPK':rq==='SPEED'?'280':'270'}"></label>`}
        ${rq!=='FREE'?`<label class="ac-fl"><span>Reason</span><select id="acWhy"><option value="">None</option><option value="WEATHER" ${f.why==='WEATHER'?'selected':''}>Due to weather</option><option value="AIRCRAFT PERFORMANCE" ${f.why==='AIRCRAFT PERFORMANCE'?'selected':''}>Due to aircraft performance</option></select></label>`:''}
        <pre class="ac-prev mono" id="acPrev">${esc(acCpText()||'REQUEST …')}</pre>
        <button class="ac-go" data-act="request">Send request</button>`;
    }
  }
  h+=`<div class="ac-err ${acars.err?'':'hidden'}" id="acErr" role="alert">${esc(acars.err)}</div></form>`;
  if(off) h=`<div class="ac-tabs" role="tablist">${tabs.map(([k,l])=>`<button role="tab" data-t="${k}" class="${acars.tab===k?'on':''}">${l}</button>`).join('')}</div>
    <div class="ac-off">${!d.hasLogon?'Sending needs your Hoppie logon code, set in the desktop app.':'Sending needs a callsign: book a flight or load a SimBrief plan.'}</div>`;
  el.innerHTML=h;
  $$('#acComp .ac-tabs button').forEach(b=>b.onclick=()=>{ acars.tab=b.dataset.t; acars.err=''; acCompDraw(); });
  if(off) return;
  const bind=(id,key,cb)=>{ const i=$('#'+id); if(i) i.oninput=()=>{ f[key]=i.value.toUpperCase(); if(cb) cb(); }; };
  const prev=()=>{ const p=$('#acPrev'); if(!p) return;
    p.textContent=acars.tab==='pdc'?`REQUEST PREDEP CLEARANCE ${d.callsign} ${(f.type||'TYPE')} TO ${(f.dest||'DEST')} AT ${(d.departure||f.pdcStation||'DEP')}${f.stand?' STAND '+f.stand:''}${f.atis?' ATIS '+f.atis:''}`:(acCpText()||'REQUEST …'); };
  bind('acTo','to',()=>{ const h=$('#acComp .ac-hint'); if(h) h.textContent=f.to==='VAOPS'?'Goes to AviatesAir dispatch. The AI dispatcher answers company questions here within a minute or two.':'A telex to any Hoppie station: a controller, another pilot\'s callsign or a company station.'; });
  bind('acText','text',()=>{ $('#acCnt').textContent=`${f.text.length}/500`; });
  bind('acPdcSt','pdcStation',prev); bind('acDest','dest',prev); bind('acType','type',prev); bind('acStand','stand',prev); bind('acAtis','atis',prev);
  bind('acIcao','icao'); bind('acStation','station'); bind('acVal','val',prev); bind('acFree','free',prev);
  const why=$('#acWhy'); if(why) why.onchange=()=>{ f.why=why.value; prev(); };
  $$('#acComp .ac-chips').forEach(c=>c.querySelectorAll('button').forEach(b=>b.onclick=()=>{ const i=$('#'+c.dataset.for); i.value=b.dataset.v; i.oninput(); }));
  $$('#acComp [data-k]').forEach(b=>b.onclick=()=>{ f.kind=b.dataset.k; acCompDraw(); });
  $$('#acComp [data-rq]').forEach(b=>b.onclick=()=>{ f.rq=b.dataset.rq; f.val=''; acCompDraw(); });
  $$('#acComp [data-ld]').forEach(b=>b.onclick=()=>{ f.lvlDir=b.dataset.ld; acCompDraw(); });
  $$('#acComp [data-act]').forEach(b=>b.onclick=async e=>{ e.preventDefault(); await acAct(b.dataset.act); });
  $('#acForm').onsubmit=e=>e.preventDefault();
  acBusy();
}
// the CPDLC request the builder makes (FANS downlink wording)
function acCpText(){
  const f=acars.f, v=(f.val||'').trim().toUpperCase(), why=f.why?` DUE TO ${f.why}`:'';
  if(f.rq==='FREE') return (f.free||'').trim().toUpperCase();
  if(!v) return '';
  if(f.rq==='LEVEL') return `REQUEST ${f.lvlDir==='CLIMB'?'CLIMB':'DESCENT'} TO ${/^\d{2,3}$/.test(v)?'FL'+v.padStart(3,'0'):v}${why}`;
  if(f.rq==='DIRECT') return `REQUEST DIRECT TO ${v}${why}`;
  if(f.rq==='SPEED') return `REQUEST ${/^M?\.?\d{2}$/.test(v)&&/^M|^\./.test(v)?'M.'+v.replace(/^M?\.?/,''):v.replace(/\s*KT$/,'')+' KT'}${why}`;
  return `REQUEST HEADING ${v.padStart(3,'0')}${why}`;
}
async function acAct(a){
  const f=acars.f, d=acars.d||{};
  if(a==='telex'){ const r=await acSend('/efb/api/acars/telex',{to:f.to,text:f.text}); if(r.ok){ f.text=''; acCompDraw(); } }
  else if(a==='pdc') await acSend('/efb/api/acars/pdc',{station:f.pdcStation,aircraftType:f.type,destination:f.dest,departure:d.departure||f.pdcStation,stand:f.stand,atis:f.atis});
  else if(a==='info') await acSend('/efb/api/acars/info',{kind:f.kind,icao:f.icao});
  else if(a==='logon'){ await acSend('/efb/api/acars/cpdlc/logon',{station:f.station||d.nextStation}); acCompDraw(); }
  else if(a==='logoff'){ await acSend('/efb/api/acars/cpdlc/logoff',{}); acCompDraw(); }
  else if(a==='request'){ const t=acCpText(); if(!t){ acars.err='Fill in the request first.'; acCompDraw(); return; }
    const r=await acSend('/efb/api/acars/cpdlc/request',{text:t}); if(r.ok){ f.val=''; f.free=''; acCompDraw(); } }
  if(matchMedia('(max-width:899px)').matches&&acars.sel&&!acars.err){ acars.pane='view'; acPane(); }
}

// ── MESSAGES APP (the same system as the desktop app) ──
// An iPad-style split view, the way crew messaging looks on a company tablet: conversations on
// the left (the airline-wide Crew Room pinned on top, then direct threads, newest first) and the
// open thread on the right. Phones show one pane at a time. Live updates arrive over the
// WebSocket and patch the panes in place, so a half-typed message, the scroll position and an
// open sheet all survive them. Broadcasts can't be marked read on the host, so the Crew Room's
// unread state is kept on this device.
const MS_CREW='#crew', MS_SEEN='efbCrewSeen', MS_MAX=2000;
const MS_QUICK=['Roger','Thanks!','On my way','Ready when you are',"What's your ETA?",'See you on frequency','Standing by'];
const MS_ICO={
  compose:`<svg viewBox="0 0 24 24"><path d="M12 4H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-6"/><path d="M17.5 3.5a2.1 2.1 0 0 1 3 3L12 15l-4 1 1-4 8.5-8.5Z"/></svg>`,
  search:`<svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>`,
  back:`<svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6"/></svg>`,
  info:`<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.5v.5"/></svg>`,
  send:`<svg viewBox="0 0 24 24"><path d="M12 19V5M5 12l7-7 7 7"/></svg>`,
  plane:`<svg viewBox="0 0 24 24"><path d="M21 15.5v-2L13.5 9V4.2a1.5 1.5 0 0 0-3 0V9L3 13.5v2l7.5-2.3V18L8 19.8V21l4-1 4 1v-1.2L13.5 18v-4.8L21 15.5Z"/></svg>`,
  copy:`<svg viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>`,
  down:`<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12l7 7 7-7"/></svg>`,
  close:`<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>`,
  crew:`<svg viewBox="0 0 24 24"><path d="M3 10v4a1 1 0 0 0 1 1h2l5 4V5L6 9H4a1 1 0 0 0-1 1Z"/><path d="M15.5 8.5a5 5 0 0 1 0 7M18.5 5.5a9 9 0 0 1 0 13"/></svg>`,
  tick:`<svg viewBox="0 0 24 24"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>`,
  clock:`<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></svg>`,
  key:`<svg viewBox="0 0 24 24"><circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l3 3M14.5 8.5l2 2"/></svg>`,
  user:`<svg viewBox="0 0 24 24"><circle cx="12" cy="8" r="4"/><path d="M4.5 20a7.5 7.5 0 0 1 15 0"/></svg>`,
};
let ms={sel:null,q:'',drafts:{},traffic:[],sheet:null,shQ:'',info:false,newFrom:null,rmArm:false,sending:false};

// ── data ──
// Host times are UTC; one without an offset is still UTC, not local.
function msDate(s){ if(!s) return new Date(0); s=String(s); return new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(s)?s:s+'Z'); }
function msCrewSeen(){ try{ return +localStorage.getItem(MS_SEEN)||0; }catch{ return 0; } }
function msCrewMark(c){ const t=c.last?+msDate(c.last.sentAt):Date.now(); if(t>msCrewSeen()){ try{ localStorage.setItem(MS_SEEN,String(t)); }catch{} } }
function msConvs(){
  const map=new Map();
  for(const f of msg.friends||[]) map.set(f.pilotId,{id:f.pilotId,name:f.pilotName||'Pilot',friend:f,msgs:[]});
  for(const m of msg.messages||[]){
    if(m.type==='Broadcast') continue;
    const peer=m.mine?m.recipientId:m.senderId; if(!peer) continue;
    let c=map.get(peer);
    // a pilot who isn't in your list can still message you (they know your id): keep their thread
    if(!c){ const t=msLive(peer); c={id:peer,name:(!m.mine&&m.senderName)||(t&&t.pilotName)||'Pilot',friend:null,msgs:[]}; map.set(peer,c); }
    if(!m.mine&&m.senderName&&!c.friend) c.name=m.senderName;
    c.msgs.push(m);
  }
  const list=[...map.values()].map(c=>({...c,last:c.msgs[c.msgs.length-1]||null,unread:c.msgs.filter(m=>!m.mine&&!m.isRead).length}));
  list.sort((a,b)=>(b.last?+msDate(b.last.sentAt):0)-(a.last?+msDate(a.last.sentAt):0)||a.name.localeCompare(b.name));
  const bc=(msg.messages||[]).filter(m=>m.type==='Broadcast');
  // first visit: everything already posted counts as seen, so the badge starts at zero
  try{ if(localStorage.getItem(MS_SEEN)==null) localStorage.setItem(MS_SEEN,String(bc.length?+msDate(bc[bc.length-1].sentAt):Date.now())); }catch{}
  const seen=msCrewSeen();
  const crew={id:MS_CREW,name:'Crew Room',crew:true,friend:null,msgs:bc,last:bc[bc.length-1]||null,
    unread:bc.filter(m=>!m.mine&&+msDate(m.sentAt)>seen).length};
  return {crew,list};
}
function msFind(id){ if(!id) return null; const {crew,list}=msConvs(); return id===MS_CREW?crew:list.find(c=>c.id===id)||null; }
function msLive(id){ return (ms.traffic||[]).find(p=>p.pilotId===id)||null; }
async function msTrafficLoad(){
  try{ ms.traffic=(await getJSON('/efb/api/traffic')).filter(p=>p.pilotId&&p.pilotId!==msg.myPilotId); }catch{ return; }
  if(activeApp!=='messages'||!$('#ms')) return;
  msListDraw(); const c=msFind(ms.sel); if(c){ const st=$('#msStat'); if(st) st.textContent=msStatus(c); if(ms.info) msInfoDraw(); }
  if(ms.sheet==='flying') msSheetBody();
}

// ── formatting ──
function initials(n){ n=(n||'').trim(); if(!n)return'?'; const p=n.split(/\s+/); return (p[0][0]+(p[1]?p[1][0]:'')).toUpperCase(); }
function msHue(id){ let h=0; for(const ch of String(id||'')) h=(h*31+ch.charCodeAt(0))>>>0; return h%360; }
function msFL(ft){ return ft>=10000?`FL${String(Math.round(ft/100)).padStart(3,'0')}`:ft>500?`${(Math.round(ft/100)*100).toLocaleString()} ft`:''; }
function msOnline(c){ return !c.crew&&(!!(c.friend&&c.friend.isOnline)||!!msLive(c.id)); }
function msAv(c,big){
  if(c.crew) return `<span class="ms-av crew${big?' big':''}">${MS_ICO.crew}</span>`;
  const on=msOnline(c), fly=!!msLive(c.id);
  return `<span class="ms-av${big?' big':''}" style="--h:${msHue(c.id)}">${esc(initials(c.name))}${on?`<i class="${fly?'fly':''}" title="${fly?'Flying':'Online'}"></i>`:''}</span>`;
}
function msFlying(t){ return [t.callsign,t.departureIcao&&t.arrivalIcao?`${t.departureIcao}→${t.arrivalIcao}`:'',msFL(t.alt)].filter(Boolean).join(' · '); }
function msStatus(c){
  if(c.crew) return 'Broadcast to every AviatesAir pilot';
  const t=msLive(c.id);
  if(t) return 'Flying '+msFlying(t);
  if(c.friend&&c.friend.isOnline) return 'Online';
  if(!c.friend) return 'Not in your pilots';
  return [c.friend.rank,c.friend.friendCode].filter(Boolean).join(' · ')||'Offline';
}
function msZ(d){ return String(d.getUTCHours()).padStart(2,'0')+String(d.getUTCMinutes()).padStart(2,'0')+'Z'; }
function msLocal(d){ return d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',hour12:false}); }
function msDay(d){
  const n=new Date(), y=new Date(n); y.setDate(n.getDate()-1);
  if(d.toDateString()===n.toDateString()) return 'Today';
  if(d.toDateString()===y.toDateString()) return 'Yesterday';
  return d.toLocaleDateString(undefined,{weekday:'short',day:'numeric',month:'short',year:d.getFullYear()===n.getFullYear()?undefined:'numeric'});
}
function msListTime(s){
  const d=msDate(s), n=new Date();
  if(d.toDateString()===n.toDateString()) return msLocal(d);
  if(n-d<6*864e5) return d.toLocaleDateString(undefined,{weekday:'short'});
  return d.toLocaleDateString(undefined,{day:'numeric',month:'short'});
}
// up to six emoji on their own render large, like iMessage
function msBig(t){ t=(t||'').trim(); return !!t&&t.length<=24&&/^[\p{Extended_Pictographic}‍️\s]+$/u.test(t)&&[...t.replace(/\s/g,'')].filter(ch=>/\p{Extended_Pictographic}/u.test(ch)).length<=6; }
async function copyText(t){
  try{ await navigator.clipboard.writeText(t); return true; }catch{}
  // the EFB is served over plain http on the LAN, where the async clipboard is unavailable
  try{ const a=document.createElement('textarea'); a.value=t; a.setAttribute('readonly',''); a.style.cssText='position:fixed;opacity:0;top:0';
    document.body.appendChild(a); a.select(); const ok=document.execCommand('copy'); a.remove(); return ok; }catch{ return false; }
}
function msCopyBtn(b,t){ b.onclick=async()=>{ if(await copyText(t)){ b.classList.add('ok'); setTimeout(()=>b.classList.remove('ok'),1300); } }; }
// my own flight as one line, for "share my flight"
function msFlightLine(){
  const s=state||{}; if(!(s.hasActiveFlight||s.callsign||s.flightNumber)) return '';
  const rt=s.departureIcao||s.arrivalIcao?`${s.departureIcao||'----'}→${s.arrivalIcao||'----'}`:'';
  const ph=s.phase&&s.phase!=='Unknown'?s.phase.toUpperCase():'';
  const gs=s.groundKts>30?`GS ${Math.round(s.groundKts)} kt`:'';
  const eta=s.eteMinutes>0?`ETA ${msZ(new Date(Date.now()+s.eteMinutes*60e3))}`:'';
  return [s.callsign||s.flightNumber,rt,s.aircraftType,ph,s.simConnected?msFL(s.altitudeFt):'',gs,eta].filter(Boolean).join(' · ');
}

// ── shell ──
function renderMessages(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  ms.info=false; ms.sheet=null; ms.rmArm=false;
  const wide=matchMedia('(min-width:761px)').matches;
  if(ms.sel&&!msFind(ms.sel)) ms.sel=null;
  // a tablet opens on the newest conversation with something unread (else the newest one),
  // like iPad Messages; a phone opens on the list
  if(!ms.sel&&wide){ const {crew,list}=msConvs(); const all=[crew,...list].filter(c=>c.last);
    const pick=all.filter(c=>c.unread).sort((a,b)=>+msDate(b.last.sentAt)-+msDate(a.last.sentAt))[0]||list[0]||crew; ms.sel=pick.id; }
  body.innerHTML=`<div class="ms" id="ms">
    <aside class="ms-side" aria-label="Conversations">
      <div class="ms-sh"><div class="ms-ttl"><b>Messages</b><small id="msSub"></small></div>
        <button class="ms-ib" id="msNew" title="New message" aria-label="New message">${MS_ICO.compose}</button></div>
      <div id="msKey"></div>
      <label class="ms-search">${MS_ICO.search}<input id="msQ" type="search" placeholder="Search pilots and messages" autocomplete="off" aria-label="Search"></label>
      <div class="ms-list" id="msList"></div>
      <div class="ms-me" id="msMe"></div>
    </aside>
    <section class="ms-main" id="msMain"></section>
    <div class="ms-sheet hidden" id="msSheet"></div>
  </div>`;
  $('#msQ').value=ms.q; $('#msQ').oninput=e=>{ ms.q=e.target.value.trim().toLowerCase(); msListDraw(); };
  $('#msNew').onclick=()=>msSheetOpen('friends');
  msListDraw(); msMeDraw(); msKeyDraw(); msMainDraw();
  msTrafficLoad();
  const iv=setInterval(msTrafficLoad,30000);
  const vis=()=>{ if(!document.hidden){ const c=msFind(ms.sel); if(c) msMarkRead(c); } };
  document.addEventListener('visibilitychange',vis);
  appCleanup=()=>{ clearInterval(iv); document.removeEventListener('visibilitychange',vis); };
}
// a WebSocket "messages" push: patch the panes, keep the draft, the scroll and any open sheet
function msRefresh(stick){
  if(activeApp!=='messages'||!$('#ms')) return;
  msListDraw(); msMeDraw(); msKeyDraw();
  const c=msFind(ms.sel);
  if(ms.sel&&!c){ ms.sel=null; msMainDraw(); return; }
  if(c&&$('#msScroll')){ msThreadDraw(stick?'bottom':''); const st=$('#msStat'); if(st) st.textContent=msStatus(c);
    if(!document.hidden) msMarkRead(c); if(ms.info) msInfoDraw(); msSendState(); }
  // don't rebuild the pilot list under someone typing in its search box
  if(ms.sheet==='friends'&&(document.activeElement||{}).id!=='msShQ') msSheetBody();
}

function msListDraw(){
  const el=$('#msList'); if(!el) return;
  const {crew,list}=msConvs(), q=ms.q;
  const hit=c=>!q||c.name.toLowerCase().includes(q)||!!(c.friend&&(c.friend.friendCode||'').toLowerCase().includes(q))||c.msgs.some(m=>(m.content||'').toLowerCase().includes(q));
  const rows=list.filter(hit);
  el.innerHTML=(hit(crew)?msRow(crew):'')
    +`<div class="ms-sec">Direct messages${list.length?`<span>${list.length}</span>`:''}</div>`
    +(rows.length?rows.map(msRow).join('')
      :`<div class="ms-none">${q?'No pilots or messages match.':'No conversations yet.<br>Tap <b>New message</b> to write to a friend, add a pilot by friend code, or pick someone who is flying now.'}</div>`);
  $$('#msList .ms-row').forEach(r=>r.onclick=()=>msSelect(r.dataset.id));
  const n=list.reduce((a,c)=>a+c.unread,0)+crew.unread, on=list.filter(msOnline).length;
  $('#msSub').textContent=(n?`${n} unread`:'No unread messages')+(on?`, ${on} online`:'');
}
function msRow(c){
  const l=c.last, draft=ms.sel!==c.id&&(ms.drafts[c.id]||'').trim();
  const who=l?(l.mine?'You: ':c.crew?(l.senderName||'Pilot').split(/\s+/)[0]+': ':''):'';
  const prev=draft?`<i>Draft</i> ${esc(draft)}`:l?esc(who+l.content):esc(c.crew?'The airline-wide channel. Say hello.':'No messages yet');
  return `<button class="ms-row${ms.sel===c.id?' on':''}${c.unread?' unread':''}${c.crew?' pin':''}" data-id="${esc(c.id)}" aria-current="${ms.sel===c.id}">
    ${msAv(c)}
    <span class="ms-rt"><span class="ms-r1"><b>${esc(c.name)}</b><time>${l?msListTime(l.sentAt):''}</time></span>
      <span class="ms-r2"><span>${prev}</span>${c.unread?`<em>${c.unread>99?'99+':c.unread}</em>`:''}</span>
      ${!c.crew&&msLive(c.id)?`<span class="ms-r3">${MS_ICO.plane}${esc(msFlying(msLive(c.id)))}</span>`:''}</span></button>`;
}
function msMeDraw(){
  const el=$('#msMe'); if(!el) return;
  el.innerHTML=`<span class="ms-av me" style="--h:${msHue(msg.myPilotId)}">${esc(initials(msg.myPilotName||'Me'))}</span>
    <span class="ms-mi"><b>${esc(msg.myPilotName||'You')}</b><small>Friend code <span class="mono">${esc(msg.myFriendCode||'not set')}</span></small></span>
    ${msg.myFriendCode?`<button class="ms-ib" id="msCopy" title="Copy my friend code" aria-label="Copy my friend code">${MS_ICO.copy}${MS_ICO.tick}</button>`:''}
    <button class="ms-ib" id="msAdd" title="Add a pilot by friend code" aria-label="Add a pilot by friend code">${MS_ICO.user}</button>`;
  if(msg.myFriendCode) msCopyBtn($('#msCopy'),msg.myFriendCode);
  $('#msAdd').onclick=()=>msSheetOpen('code');
}
function msKeyDraw(){
  const el=$('#msKey'); if(!el) return;
  el.innerHTML=msg.hasKey===false?`<div class="ms-key">${MS_ICO.key}<span><b>Messaging is offline</b>Add your ACARS key in the desktop app (Settings › ACARS) to send and receive messages.</span></div>`:'';
}
function msSelect(id){
  ms.sel=id; ms.info=false; ms.rmArm=false;
  msListDraw(); msMainDraw();
  const ta=$('#msText'); if(ta&&matchMedia('(min-width:761px) and (pointer:fine)').matches) ta.focus();
}
function msMarkRead(c){
  if(c.crew){ if(c.unread){ msCrewMark(c); msListDraw(); } return; }
  const ids=c.msgs.filter(m=>!m.mine&&!m.isRead).map(m=>m.id); if(!ids.length) return;
  for(const m of msg.messages) if(ids.includes(m.id)) m.isRead=true;
  msg.unreadCount=Math.max(0,(msg.unreadCount||0)-ids.length); renderBadges(); msListDraw();
  postJSON('/efb/api/messages/read',{ids}).catch(()=>{});
}

// ── thread ──
function msMainDraw(){
  const main=$('#msMain'), root=$('#ms'); if(!main) return;
  const c=msFind(ms.sel);
  root.classList.toggle('open',!!c);
  if(!c){
    main.innerHTML=`<div class="ms-empty">${MS_ICO.compose}<b>No conversation open</b><span>Pick one on the left, or start a new one.</span>
      <button class="ms-btn pri" id="msEmptyNew">New message</button></div>`;
    $('#msEmptyNew').onclick=()=>msSheetOpen('friends'); return;
  }
  // where "New messages" goes, fixed when the thread opens (it is marked read straight away)
  const fu=c.crew?c.msgs.find(m=>!m.mine&&+msDate(m.sentAt)>msCrewSeen()):c.msgs.find(m=>!m.mine&&!m.isRead);
  ms.newFrom=fu?fu.id:null;
  const off=msg.hasKey===false;
  main.innerHTML=`
    <header class="ms-th">
      <button class="ms-ib ms-back" id="msBack" aria-label="Back to conversations">${MS_ICO.back}</button>
      <button class="ms-who" id="msWho" aria-label="Details for ${esc(c.name)}">${msAv(c)}<span><b>${esc(c.name)}</b><small id="msStat">${esc(msStatus(c))}</small></span></button>
      <button class="ms-ib" id="msInfoB" title="Details" aria-label="Details" aria-pressed="false">${MS_ICO.info}</button>
    </header>
    <div class="ms-body">
      <div class="ms-scroll" id="msScroll" aria-live="polite"></div>
      <button class="ms-jump hidden" id="msJump">${MS_ICO.down}New messages</button>
      <aside class="ms-info hidden" id="msInfo" aria-label="Details"></aside>
    </div>
    <div class="ms-quick" id="msQuick">${MS_QUICK.map(q=>`<button type="button" data-q="${esc(q)}">${esc(q)}</button>`).join('')}</div>
    <div class="ms-err hidden" id="msErr" role="alert"></div>
    <form class="ms-comp" id="msComp">
      <button type="button" class="ms-ib" id="msShare" title="Share my flight" aria-label="Share my flight">${MS_ICO.plane}</button>
      <div class="ms-field"><textarea id="msText" rows="1" maxlength="${MS_MAX}" ${off?'disabled':''}
        placeholder="${off?'Messaging needs your ACARS key':c.crew?'Message the whole airline':'Message '+esc(c.name)}" aria-label="Message"></textarea>
        <span class="ms-cnt hidden" id="msCnt"></span></div>
      <button type="submit" class="ms-send" id="msSend" aria-label="Send">${MS_ICO.send}</button>
    </form>`;
  $('#msBack').onclick=()=>{ ms.sel=null; ms.info=false; $('#ms').classList.remove('open'); msListDraw(); };
  $('#msWho').onclick=$('#msInfoB').onclick=()=>{ ms.info=!ms.info; ms.rmArm=false; msInfoDraw(); };
  const sc=$('#msScroll');
  sc.onscroll=()=>{ if(sc.scrollHeight-sc.scrollTop-sc.clientHeight<60) $('#msJump').classList.add('hidden'); };
  $('#msJump').onclick=()=>{ sc.scrollTo({top:sc.scrollHeight,behavior:'smooth'}); $('#msJump').classList.add('hidden'); };
  const ta=$('#msText');
  ta.value=ms.drafts[c.id]||'';
  ta.oninput=()=>{ ms.drafts[c.id]=ta.value; msGrow(); msSendState(); msErr(''); };
  ta.onkeydown=e=>{ if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing){ e.preventDefault(); msSend(); } };
  $('#msComp').onsubmit=e=>{ e.preventDefault(); msSend(); };
  $$('#msQuick button').forEach(b=>b.onclick=()=>msInsert(b.dataset.q));
  const sh=$('#msShare'), line=msFlightLine();
  sh.disabled=!line||off; if(!line) sh.title='Share my flight (no active flight)';
  sh.onclick=()=>{ const l=msFlightLine(); if(l) msInsert(l); };
  if(off) $$('#msQuick button').forEach(b=>b.disabled=true);
  msThreadDraw('open'); msGrow(); msSendState(); msMarkRead(c);
}
function msGrow(){ const ta=$('#msText'); if(!ta) return; ta.style.height='auto'; ta.style.height=Math.min(ta.scrollHeight,150)+'px'; }
function msInsert(t){
  const ta=$('#msText'); if(!ta||ta.disabled) return;
  const v=ta.value, a=ta.selectionStart??v.length, b=ta.selectionEnd??v.length;
  const pre=v.slice(0,a), sp=pre&&!/\s$/.test(pre)?' ':'';
  ta.value=(pre+sp+t+v.slice(b)).slice(0,MS_MAX); const p=Math.min(pre.length+sp.length+t.length,MS_MAX);
  ta.focus(); ta.setSelectionRange(p,p); ta.oninput();
}
function msSendState(){
  const ta=$('#msText'), btn=$('#msSend'), cnt=$('#msCnt'); if(!ta||!btn) return;
  const n=ta.value.length;
  btn.disabled=ms.sending||ta.disabled||!ta.value.trim();
  btn.classList.toggle('busy',ms.sending);
  cnt.classList.toggle('hidden',n<MS_MAX-400); cnt.classList.toggle('warn',n>MS_MAX-100); cnt.textContent=`${n} / ${MS_MAX}`;
}
function msErr(t){ const e=$('#msErr'); if(!e) return; e.textContent=t; e.classList.toggle('hidden',!t); }
async function msSend(){
  const c=msFind(ms.sel), ta=$('#msText'); if(!c||!ta||ms.sending||ta.disabled) return;
  const t=ta.value.trim(); if(!t) return;
  ms.sending=true; msSendState();
  let r; try{ r=await postJSON('/efb/api/messages/send',c.crew?{broadcast:true,content:t}:{recipientId:c.id,content:t}); }
  catch{ r={ok:false,error:'Could not reach the desktop app.'}; }
  ms.sending=false;
  if(r&&r.ok){
    delete ms.drafts[c.id]; if(ta.isConnected){ ta.value=''; msGrow(); } msErr('');
    try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)){ msg=m; renderBadges(); } }catch{}   // the WS pushes it too; this covers REST polling
    msRefresh(true);
  } else msErr((r&&r.error)||'Message not sent.');
  msSendState(); if(ta.isConnected) ta.focus();
}
// mode: 'open' (jump to the new-messages line, else the bottom), 'bottom', or '' (stay put
// unless already at the bottom, and offer a jump button when something new arrives above it)
function msThreadDraw(mode){
  const sc=$('#msScroll'), c=msFind(ms.sel); if(!sc||!c) return;
  const atBottom=sc.scrollHeight-sc.scrollTop-sc.clientHeight<60, before=+sc.dataset.n||0;
  const M=c.msgs;
  const grp=(a,b)=>!!(a&&b)&&a.senderId===b.senderId&&a.type===b.type&&b.id!==ms.newFrom
    &&Math.abs(msDate(b.sentAt)-msDate(a.sentAt))<5*60e3&&msDate(a.sentAt).toDateString()===msDate(b.sentAt).toDateString();
  let h=!M.length||c.crew?`<div class="ms-hello">${M.length?'':msAv(c,true)+`<b>${esc(c.name)}</b>`}<span>${c.crew
    ?'Everything posted here reaches every AviatesAir pilot. Messages are filtered for language.'
    :`No messages with ${esc(c.name)} yet.`}</span></div>`:'';
  let day='';
  M.forEach((m,i)=>{
    const d=msDate(m.sentAt), prev=M[i-1], next=M[i+1];
    if(d.toDateString()!==day){ day=d.toDateString(); h+=`<div class="ms-day"><span>${msDay(d)}</span></div>`; }
    if(m.id===ms.newFrom) h+=`<div class="ms-new"><span>New messages</span></div>`;
    const first=!grp(prev,m), last=!grp(m,next), them=!m.mine, big=msBig(m.content);
    const tick=m.mine?(m.synced?`<i class="ms-ok" title="Sent">${MS_ICO.tick}</i>`:`<i class="ms-qd" title="Queued: it goes out when the desktop app next reaches the AviatesAir server">${MS_ICO.clock}</i>`):'';
    h+=`<div class="ms-m ${them?'them':'me'}${first?' first':''}${last?' last':''}">
      ${them&&c.crew?`<span class="ms-ga">${last?msAv({id:m.senderId,name:m.senderName||'Pilot'}):''}</span>`:''}
      <div class="ms-mc">${them&&c.crew&&first?`<span class="ms-nm" style="--h:${msHue(m.senderId)}">${esc(m.senderName||'Pilot')}</span>`:''}
        <div class="ms-b${big?' big':''}${m.type==='System'?' sys':''}">${esc(m.content)}</div>
        ${last?`<span class="ms-tm" title="${esc(d.toUTCString())}">${msZ(d)} <small>${msLocal(d)} local</small>${tick}</span>`:''}</div></div>`;
  });
  sc.innerHTML=h; sc.dataset.n=M.length;
  if(mode==='open'){ const nw=sc.querySelector('.ms-new'); sc.scrollTop=nw?Math.max(0,nw.offsetTop-60):sc.scrollHeight; }
  else if(mode==='bottom'||atBottom) sc.scrollTop=sc.scrollHeight;
  else if(M.length>before&&M[M.length-1]&&!M[M.length-1].mine) $('#msJump').classList.remove('hidden');
}

// ── details drawer ──
function msInfoDraw(){
  const el=$('#msInfo'), c=msFind(ms.sel); if(!el||!c) return;
  el.classList.toggle('hidden',!ms.info); $('#msInfoB').setAttribute('aria-pressed',ms.info);
  if(!ms.info) return;
  const row=(k,v)=>v?`<div class="ms-kv"><span>${k}</span><b>${v}</b></div>`:'';
  const mine=c.msgs.filter(m=>m.mine).length, first=c.msgs[0];
  let h=`<button class="ms-ib ms-ix" id="msInfoX" aria-label="Close details">${MS_ICO.close}</button>
    <div class="ms-ih">${msAv(c,true)}<b>${esc(c.name)}</b><small>${esc(msStatus(c))}</small></div>`;
  if(c.crew){
    const who=new Set(c.msgs.filter(m=>!m.mine).map(m=>m.senderId)).size;
    h+=`<div class="ms-ig">${row('Channel','All AviatesAir pilots')}${row('Messages',String(c.msgs.length))}${row('Pilots posting',String(who))}${row('Yours',String(mine))}</div>
      <p class="ms-note">Posts go to everyone who is signed in to the tracker. Use a direct message for anything personal.</p>`;
  } else {
    const t=msLive(c.id), f=c.friend;
    h+=`<div class="ms-ig">${f&&f.friendCode?`<div class="ms-kv"><span>Friend code</span><b class="mono">${esc(f.friendCode)}<button class="ms-ib sm" id="msFc" aria-label="Copy friend code">${MS_ICO.copy}${MS_ICO.tick}</button></b></div>`:''}
      ${row('Rank',f&&f.rank?esc(f.rank):'')}${row('Status',t?'Flying':msOnline(c)?'Online':'Offline')}</div>`;
    if(t) h+=`<div class="ms-sl">Live flight</div><div class="ms-ig">${row('Callsign',esc(t.callsign||'—'))}
      ${row('Route',t.departureIcao||t.arrivalIcao?esc(`${t.departureIcao||'----'} → ${t.arrivalIcao||'----'}`):'')}
      ${row('Phase',esc(t.phase||''))}${row('Altitude',esc(msFL(t.alt)||'Ground'))}${row('Ground speed',t.speed?`${t.speed} kt`:'')}</div>`;
    h+=`<div class="ms-sl">Conversation</div><div class="ms-ig">${row('Messages',String(c.msgs.length))}${row('From you',String(mine))}
      ${row('Since',first?esc(msDate(first.sentAt).toLocaleDateString(undefined,{day:'numeric',month:'short',year:'numeric'})):'')}</div>`;
    h+=f?`<button class="ms-btn danger${ms.rmArm?' arm':''}" id="msRm">${ms.rmArm?'Tap again to remove':'Remove from my pilots'}</button>
        <p class="ms-note">Removing keeps the message history. They can still message you.</p>`
      :`<button class="ms-btn pri" id="msKeep">Add to my pilots</button>`;
  }
  el.innerHTML=h;
  $('#msInfoX').onclick=()=>{ ms.info=false; msInfoDraw(); };
  if($('#msFc')) msCopyBtn($('#msFc'),c.friend.friendCode);
  if($('#msRm')) $('#msRm').onclick=async()=>{
    if(!ms.rmArm){ ms.rmArm=true; msInfoDraw(); setTimeout(()=>{ if(ms.rmArm){ ms.rmArm=false; msInfoDraw(); } },3500); return; }
    ms.rmArm=false; await postJSON('/efb/api/friends/remove',{pilotId:c.id}).catch(()=>{});
    try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)) msg=m; }catch{} ms.info=false; msRefresh(); if($('#msInfo')) msInfoDraw();
  };
  if($('#msKeep')) $('#msKeep').onclick=async()=>{
    await postJSON('/efb/api/friends/add-pilot',{pilotId:c.id,pilotName:c.name}).catch(()=>{});
    try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)) msg=m; }catch{} msRefresh();
  };
}

// ── new message sheet: friends · flying now · friend code ──
function msSheetOpen(tab){
  ms.sheet=tab; ms.shQ='';
  const el=$('#msSheet'); el.classList.remove('hidden');
  el.innerHTML=`<div class="ms-card" role="dialog" aria-modal="true" aria-label="New message">
    <div class="ms-ch"><b>New message</b><button class="ms-ib" id="msShX" aria-label="Close">${MS_ICO.close}</button></div>
    <div class="ms-seg" role="tablist">${[['friends','My pilots'],['flying','Flying now'],['code','Friend code']].map(([k,l])=>
      `<button role="tab" data-t="${k}" aria-selected="${k===tab}" class="${k===tab?'on':''}">${l}</button>`).join('')}</div>
    <div class="ms-cb" id="msShBody"></div></div>`;
  const close=()=>{ ms.sheet=null; el.classList.add('hidden'); el.innerHTML=''; };
  $('#msShX').onclick=close;
  el.onclick=e=>{ if(e.target===el) close(); };
  el.onkeydown=e=>{ if(e.key==='Escape'){ e.preventDefault(); close(); } };
  $$('#msSheet .ms-seg button').forEach(b=>b.onclick=()=>{ ms.sheet=b.dataset.t;
    $$('#msSheet .ms-seg button').forEach(x=>{ x.classList.toggle('on',x===b); x.setAttribute('aria-selected',x===b); }); msSheetBody(); });
  msSheetBody();
  if(tab==='flying') msTrafficLoad();
}
function msSheetBody(){
  const el=$('#msShBody'); if(!el) return;
  const pick=id=>{ ms.sheet=null; $('#msSheet').classList.add('hidden'); $('#msSheet').innerHTML=''; msSelect(id); };
  if(ms.sheet==='code'){
    el.innerHTML=`<p class="ms-hint">Ask the other pilot for their friend code. It is under their name in their Messages app.</p>
      <form class="ms-code" id="msCodeF"><input id="msCode" maxlength="9" placeholder="XXXX-XXXX" autocomplete="off" autocapitalize="characters" spellcheck="false" aria-label="Friend code">
        <button class="ms-btn pri" id="msCodeGo" disabled>Add pilot</button></form>
      <div class="ms-err hidden" id="msCodeErr" role="alert"></div>
      ${msg.myFriendCode?`<div class="ms-share"><span>Your code</span><b class="mono">${esc(msg.myFriendCode)}</b><button class="ms-ib" id="msShCopy" aria-label="Copy my friend code">${MS_ICO.copy}${MS_ICO.tick}</button></div>`:''}`;
    const inp=$('#msCode'), go=$('#msCodeGo'), err=$('#msCodeErr');
    inp.oninput=()=>{ const v=inp.value.toUpperCase().replace(/[^A-Z0-9]/g,'').slice(0,8); inp.value=v.length>4?v.slice(0,4)+'-'+v.slice(4):v;
      go.disabled=v.length!==8; err.classList.add('hidden'); };
    if(msg.myFriendCode) msCopyBtn($('#msShCopy'),msg.myFriendCode);
    $('#msCodeF').onsubmit=async e=>{ e.preventDefault(); if(go.disabled) return;
      if(msg.myFriendCode&&inp.value===msg.myFriendCode){ err.textContent="That's your own friend code."; err.classList.remove('hidden'); return; }
      go.disabled=true; go.textContent='Adding…';
      let r; try{ r=await postJSON('/efb/api/friends/add',{code:inp.value}); }catch{ r={error:'Could not reach the desktop app.'}; }
      go.textContent='Add pilot'; go.disabled=false;
      if(!r||r.error||!r.pilotId){ err.textContent=(r&&r.error)||'Could not add that friend code.'; err.classList.remove('hidden'); return; }
      try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)) msg=m; }catch{} msListDraw(); pick(r.pilotId); };
    setTimeout(()=>inp.focus(),50); return;
  }
  if(ms.sheet==='flying'){
    const L=ms.traffic||[];
    el.innerHTML=L.length?`<p class="ms-hint">AviatesAir pilots in the air or on the ground right now. Tap one to message them.</p><div class="ms-pl">${L.map(t=>
      `<button class="ms-pr" data-id="${esc(t.pilotId)}" data-n="${esc(t.pilotName||t.callsign||'Pilot')}">${msAv({id:t.pilotId,name:t.pilotName||t.callsign})}
        <span><b>${esc(t.pilotName||'Pilot')}</b><small>${esc(msFlying(t)||'On the ground')}${t.phase?' · '+esc(t.phase):''}</small></span></button>`).join('')}</div>`
      :`<div class="ms-none">No other AviatesAir pilots are flying right now.</div>`;
    $$('#msShBody .ms-pr').forEach(b=>b.onclick=async()=>{
      const id=b.dataset.id;
      if(!(msg.friends||[]).some(f=>f.pilotId===id)){ await postJSON('/efb/api/friends/add-pilot',{pilotId:id,pilotName:b.dataset.n}).catch(()=>{});
        try{ const m=await getJSON('/efb/api/messages'); if(m&&Array.isArray(m.messages)) msg=m; }catch{} }
      pick(id); });
    return;
  }
  const F=(msg.friends||[]).filter(f=>!ms.shQ||(f.pilotName||'').toLowerCase().includes(ms.shQ)||(f.friendCode||'').toLowerCase().includes(ms.shQ));
  el.innerHTML=`<label class="ms-search">${MS_ICO.search}<input id="msShQ" type="search" placeholder="Find a pilot" autocomplete="off" aria-label="Find a pilot"></label>
    <div class="ms-pl">${`<button class="ms-pr" data-id="${MS_CREW}">${msAv({crew:true})}<span><b>Crew Room</b><small>Broadcast to every AviatesAir pilot</small></span></button>`}
    ${F.map(f=>{ const c={id:f.pilotId,name:f.pilotName||'Pilot',friend:f};
      return `<button class="ms-pr" data-id="${esc(f.pilotId)}">${msAv(c)}<span><b>${esc(c.name)}</b><small>${esc(msStatus(c))}</small></span></button>`; }).join('')}</div>
    ${(msg.friends||[]).length?'':`<div class="ms-none">You haven't added any pilots yet. Use <b>Friend code</b> or <b>Flying now</b>.</div>`}`;
  const q=$('#msShQ'); q.value=ms.shQ;
  q.oninput=()=>{ ms.shQ=q.value.trim().toLowerCase(); const p=q.selectionStart; msSheetBody(); const n=$('#msShQ'); n.focus(); n.setSelectionRange(p,p); };
  $$('#msShBody .ms-pr').forEach(b=>b.onclick=()=>pick(b.dataset.id));
}

// ── LANDING REPORT APP ──
// A flight-data-monitoring touchdown report on the SHARED LandingReport projection (the same
// one the desktop touchdown dialog renders): score card, touchdown parameters against the
// AviatesAir FDM event limits, the exceedance events, the approach profile (altitude with
// the 3° reference and the 1000/500 ft gates, IAS/GS, V/S, pitch/bank — scrub to read any
// point), a touchdown-zone runway diagram and the stabilised-approach gates. Reads persisted
// history, so any landing can be re-opened; `latest` carries its trace, older ones are
// fetched by id.
let lr={sel:null, cache:{}, apt:{}, axis:'dist', ro:null};
const LR_LIM={hardC:600,hardW:800,pitC:7.5,pitW:9,bankC:3,bankW:5,crab:5,xw:25,tw:10};
const lrCls=(v,c,w)=>v>=w?'bad':v>=c?'warn':'';
const LR_MON=['JAN','FEB','MAR','APR','MAY','JUN','JUL','AUG','SEP','OCT','NOV','DEC'];
const lrDate=iso=>{ const d=new Date(iso); if(isNaN(d)) return '';
  return `${String(d.getUTCDate()).padStart(2,'0')} ${LR_MON[d.getUTCMonth()]} ${d.toISOString().substr(11,5)}Z`; };
const ftM=ft=>`${num(ft)} ft <small>${num(ft*0.3048)} m</small>`;
async function renderLanding(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  if(!body.querySelector('.fx')) body.innerHTML=`<div class="fx"><div class="empty" style="margin:auto">Loading the landing report…</div></div>`;
  let d; try{ d=await getJSON('/efb/api/landings'); }
  catch{ body.innerHTML=`<div class="fx"><div class="empty" style="margin:auto"><b>Landing reports unavailable</b>Couldn't reach the desktop app.</div></div>`; return; }
  if(activeApp!=='landing') return;
  if(!d.hasLanding){ body.innerHTML=`<div class="fx"><div class="empty" style="margin:auto"><b>No landings yet</b>
    Your touchdown report appears here after you land, with the approach profile, touchdown zone and flight data events.</div></div>`; return; }
  const hist=d.history||[];
  lr.cache[d.latest.id]=d.latest;
  if(!lr.sel||!hist.some(h=>h.id===lr.sel)) lr.sel=d.latest.id;
  let r=lr.cache[lr.sel];
  if(!r){ try{ r=lr.cache[lr.sel]=await getJSON('/efb/api/landings/'+encodeURIComponent(lr.sel)); }catch{ r=hist.find(h=>h.id===lr.sel)||d.latest; } }
  if(activeApp!=='landing') return;
  const icao=(r.airportIcao||'').trim().toUpperCase();
  if(/^[A-Z0-9]{4}$/.test(icao)&&!(icao in lr.apt)){
    lr.apt[icao]=null;
    getJSON('/efb/api/airport?icao='+icao).then(a=>{ lr.apt[icao]=a; if(activeApp==='landing'&&lr.sel===r.id){ lrDrawRunway(r); heroPhoto($('#lrHero'),a&&a.name||icao); } })
      .catch(()=>{ if(activeApp==='landing') heroPhoto($('#lrHero'),icao); });
  }
  lrPaint(body,r,hist,d.latest.id);
}
function lrPaint(body,r,hist,latestId){
  const vs=Math.abs(r.verticalSpeedFpm), hw=r.headwindKts, crab=r.crabAngleDeg;
  const warnN=r.events.filter(e=>e.severity==='warning').length, cauN=r.events.filter(e=>e.severity==='caution').length;
  const tone=c=>c==='bad'?'tone-bad':c==='warn'?'tone-warn':'';
  const w=(l,v,u,cls='',sub='')=>`<div class="w"><h4>${l}</h4><div class="v ${tone(cls)}">${v}${u?`<small>${u}</small>`:''}</div>${sub?`<p>${sub}</p>`:''}</div>`;
  const icao=(r.airportIcao||'').trim().toUpperCase(), apt=lr.apt[icao];
  const chips=[[r.stableApproach?'Stable approach':'Unstable approach',r.stableApproach?'ok':'bad'],[r.flareDetected?'Flared':'No flare',r.flareDetected?'ok':''],
    [r.bounceCount?`${r.bounceCount} bounce${r.bounceCount>1?'s':''}`:'No bounce',r.bounceCount?'bad':'ok'],
    ...(r.isPersonalBest?[['Personal best','best']]:r.totalLandings>1?[[`Number ${r.rankAmongLandings} of ${r.totalLandings}, average ${r.averageScore}`,'']]:[])];
  body.innerHTML=`<div class="fx"><div class="fx-scroll">
    <header class="hero" id="lrHero"><div class="hero-tx"><span class="brand">${esc([r.flightNumber,`${icao} runway ${r.runway}`].filter(Boolean).join(', '))}</span>
        <h1>${esc(String(r.butterRating||r.grade||'').toLowerCase().replace(/^./,c=>c.toUpperCase()))} landing</h1><p>${esc(r.butterBlurb||'')}</p></div>
      <div class="lr-score"><b>${r.score}</b><span class="lr-grade" style="--c:${esc(r.gradeColor)}">${esc(r.grade)}</span></div>
      <a class="hero-cr" target="_blank" rel="noopener"></a></header>
    <div class="fx-pad lr-bar">
      <label class="lr-pick"><span>Landing</span><select id="lrSel">${hist.map(h=>`<option value="${esc(h.id)}" ${h.id===r.id?'selected':''}>${lrDate(h.timestampUtc)}, ${esc(h.airportIcao)} ${esc(h.runway)}, ${h.score}${h.id===latestId?' (latest)':''}</option>`).join('')}</select></label>
      <div class="lr-chips">${chips.map(([t,c])=>`<span class="${c}">${esc(t)}</span>`).join('')}</div>
    </div>
    <div class="fx-pad">
      ${r.requiresReview?`<div class="lr-review"><b>Held for review</b><span>${r.complianceViolations.map(esc).join('. ')}</span></div>`:''}
      <div class="gl-h">Touchdown<span class="gl-hr">${[lrDate(r.timestampUtc),r.aircraftType,r.fuelLbs?fmtWl(r.fuelLbs)+' on board':''].filter(Boolean).map(esc).join(', ')}</span></div>
      <div class="wg">
        ${w('Rate of descent',fmtN(vs),'fpm',lrCls(vs,LR_LIM.hardC,LR_LIM.hardW),r.maxSinkBelow1000Fpm?`Most below 1,000 ft: ${fmtN(r.maxSinkBelow1000Fpm)} fpm`:'')}
        ${w('Airspeed',fmtN(r.iasKts),'kt','',`Ground speed ${fmtN(r.groundSpeedKts)} kt`)}
        ${w('Pitch',num(r.pitchDeg,1)+'°','',r.pitchDeg>=LR_LIM.pitW?'bad':r.pitchDeg>=LR_LIM.pitC||r.pitchDeg<0?'warn':'',`Bank ${num(Math.abs(r.bankDeg),1)}°`)}
        ${w('Wind',r.windSpeedKts>0?`${String(Math.round(r.windDirectionDeg)).padStart(3,'0')}° ${fmtN(r.windSpeedKts)}`:'Calm',r.windSpeedKts>0?'kt':'',hw<=-LR_LIM.tw||r.crosswindKts>=LR_LIM.xw?'warn':'',
            `${hw<0?'Tailwind':'Headwind'} ${fmtN(Math.abs(hw))}, crosswind ${fmtN(r.crosswindKts)}${crab!=null?`, crab ${num(Math.abs(crab),1)}°`:''}`)}
      </div>
      <div class="fl-sec">
        <div><div class="gl-h">Flight data events${warnN||cauN?`<span class="gl-hr">${[warnN?warnN+' warning'+(warnN>1?'s':''):'',cauN?cauN+' caution'+(cauN>1?'s':''):''].filter(Boolean).join(', ')}</span>`:''}</div>
          <div class="gl">${r.events.length?r.events.map(e=>`<div class="row lr-e"><span class="lr-sev ${e.severity}">${e.severity==='warning'?'Warning':e.severity==='caution'?'Caution':'Note'}</span>
            <span class="k">${esc(e.title)}<small>${esc(e.detail)}</small></span></div>`).join('')
            :`<div class="row lr-e"><span class="lr-sev clear">Clear</span><span class="k">No exceedances<small>Every monitored value stayed inside the AviatesAir FDM limits.</small></span></div>`}</div></div>
        <div><div class="gl-h">Score</div><div class="gl">${r.factors.map(f=>`<div class="row lr-f"><span class="k">${esc(f.label)}</span><i><em style="width:${f.pct.toFixed(1)}%;background:${f.color}"></em></i>
          <span class="val">${num(f.score)}<small>of ${num(f.max)}</small></span></div>`).join('')}</div></div>
      </div>
      <div class="gl-h">Approach profile<span class="gl-hr lr-read" id="lrRead"></span></div>
      <section class="lr-scope">
        <div class="lr-scope-h"><span>Drag across the chart to read any point</span><span class="sg sm">${[['dist','Distance'],['time','Time']].map(([k,t])=>`<button data-ax="${k}" class="${lr.axis===k?'on':''}">${t}</button>`).join('')}</span></div>
        <div class="lr-chart" id="lrChart"></div>
      </section>
      <div class="fl-sec">
        <div><div class="gl-h">Touchdown zone<span class="gl-hr" id="lrRwyH"></span></div>
          <section class="lr-scope"><div class="lr-rwy" id="lrRwy"></div></section><div class="lr-rs" id="lrRs"></div></div>
        <div><div class="gl-h">Stabilised approach<span class="gl-hr">Gear, flaps, sink 1,000 fpm, bank 10°</span></div>
          <div class="gl">${(r.gates.length?r.gates:[{aglFt:1000},{aglFt:500}]).map(g=>!g.recorded?`<div class="row"><span class="k">${fmtN(g.aglFt)} ft<small>Not recorded</small></span><span class="lr-sev">No data</span></div>`
            :`<div class="row"><span class="k">${fmtN(g.aglFt)} ft<small><span class="${g.speedOk===false?'tone-bad':''}">${fmtN(g.iasKts)} kt</span>, <span class="${g.sinkOk?'':'tone-bad'}">${fmtN(g.vsFpm)} fpm</span>,
              <span class="${g.configOk?'':'tone-bad'}">gear ${g.gearDown?'down':'up'}, flaps ${fmtN(g.flapsPct)}%</span>, <span class="${g.bankOk?'':'tone-bad'}">bank ${num(Math.abs(g.bankDeg),1)}°</span></small></span>
              <span class="lr-sev ${g.stable?'clear':'warning'}">${g.stable?'Stable':'Unstable'}</span></div>`).join('')}</div></div>
      </div>
      <p class="gl-f">Event limits are AviatesAir's FDM programme (hard landing ${fmtN(LR_LIM.hardW)} fpm, touchdown zone 200 to 3,000 ft, centreline 60 ft, stabilised by 1,000 ft), not aircraft limitations. Training-grade analysis for simulation use.</p>
    </div></div></div>`;
  if(apt!==null&&apt!==undefined) heroPhoto($('#lrHero'),apt.name||icao);
  $('#lrSel').onchange=e=>{ lr.sel=e.target.value; renderLanding(); };
  $$('#appBody [data-ax]').forEach(b=>b.onclick=()=>{ lr.axis=b.dataset.ax; $$('#appBody [data-ax]').forEach(x=>x.classList.toggle('on',x===b)); lrDrawProfile(r); });
  lrDrawProfile(r); lrDrawRunway(r);
  if(lr.ro) lr.ro.disconnect();
  let rw=$('#lrChart').clientWidth;
  lr.ro=new ResizeObserver(()=>{ const c=$('#lrChart'); if(!c||Math.abs(c.clientWidth-rw)<4)return; rw=c.clientWidth; lrDrawProfile(r); lrDrawRunway(r); });
  lr.ro.observe($('#lrChart'));
  appCleanup=()=>{ if(lr.ro){ lr.ro.disconnect(); lr.ro=null; } };
}
function lrNice(lo,hi,n){ const span=Math.max(1e-6,hi-lo), raw=span/n, mag=Math.pow(10,Math.floor(Math.log10(raw)));
  const step=[1,2,2.5,5,10].map(m=>m*mag).find(s=>s>=raw)||10*mag; return {lo:Math.floor(lo/step)*step, hi:Math.ceil(hi/step)*step, step}; }
function lrDrawProfile(r){
  const el=$('#lrChart'); if(!el) return;
  let pts=r.trace||[];
  if(pts.length<3){ el.innerHTML=`<div class="lr-none">No approach trace for this landing. Profiles are recorded for landings flown with this version onward.</div>`; $('#lrRead').textContent=''; return; }
  const hasDist=pts.some(p=>p.distNm!==0), axis=lr.axis==='dist'&&hasDist?'dist':'time';
  const X=p=>axis==='dist'?p.distNm:p.t;
  const i0=Math.max(0,pts.findIndex(p=>p.aglFt<=3000)); pts=pts.slice(i0);
  const x0=X(pts[0]), x1=Math.max(X(pts[pts.length-1]),x0+1e-3);
  const W=Math.max(320,el.clientWidth), L=50, R=12, pw=W-L-R;
  const sx=v=>L+(v-x0)/(x1-x0)*pw;
  const H1=132,H2=70,H3=70,H4=62,G=20, y1=10, y2=y1+H1+G, y3=y2+H2+G, y4=y3+H3+G, H=y4+H4+26;
  const before=pts.filter(p=>p.t<0);
  const path=(sel,sy,list=pts)=>list.map((p,i)=>`${i?'L':'M'}${sx(X(p)).toFixed(1)} ${sy(sel(p)).toFixed(1)}`).join('');
  let svg='', clip='', lastClip='';
  const cp=()=>`clip-path="url(#${lastClip})"`;
  const panel=(y,h,lo,hi,n,label,fmt=v=>v)=>{ const k=lrNice(lo,hi,n), sy=v=>y+h-(v-k.lo)/(k.hi-k.lo)*h;
    const cid='lrc'+y; clip+=`<clipPath id="${cid}"><rect x="${L}" y="${y}" width="${pw}" height="${h}"/></clipPath>`; lastClip=cid;
    let g=`<rect x="${L}" y="${y}" width="${pw}" height="${h}" class="pf-bg"/>`;
    for(let v=k.lo;v<=k.hi+1e-9;v+=k.step){ const yy=sy(v).toFixed(1);
      g+=`<line x1="${L}" x2="${L+pw}" y1="${yy}" y2="${yy}" class="pf-grid"/><text x="${L-6}" y="${+yy+3}" class="pf-ax" text-anchor="end">${fmt(Math.round(v*10)/10)}</text>`; }
    g+=`<text x="${L+6}" y="${y+12}" class="pf-lbl">${label}</text>`;
    svg+=g; return sy; };
  // 1 — altitude AGL with the 3° reference path and the gates
  const aMax=Math.max(...pts.map(p=>p.aglFt),600);
  const sa=panel(y1,H1,0,aMax,4,'ALT FT AGL',v=>num(v));
  if(axis==='dist'){
    // gp(x) = (xa − x)·k: the 3° path through 50 ft over the threshold (or through the TD point)
    const k=Math.tan(3*Math.PI/180)*6076.12, thr=r.touchdownDistanceFt?-r.touchdownDistanceFt/6076.12:null;
    const xa=thr!=null?thr+50/k:0, gp=x=>(xa-x)*k, top=aMax*1.05, xs=Math.max(x0,xa-top/k), xe=Math.min(xa,x1);
    if(xs<xe) svg+=`<line x1="${sx(xs).toFixed(1)}" y1="${sa(gp(xs)).toFixed(1)}" x2="${sx(xe).toFixed(1)}" y2="${sa(gp(xe)).toFixed(1)}" class="pf-gp"/>
      <text x="${(sx(xs)+8).toFixed(1)}" y="${(sa(gp(xs))+16).toFixed(1)}" class="pf-gpt">3° REF${thr!=null?'':' (THROUGH TD)'}</text>`;
  }
  [1000,500].forEach(g=>{ if(g>aMax) return; const yy=sa(g).toFixed(1);
    svg+=`<line x1="${L}" x2="${L+pw}" y1="${yy}" y2="${yy}" class="pf-gate"/><text x="${L+pw-4}" y="${+yy-4}" class="pf-gatet" text-anchor="end">${g} FT GATE</text>`;
    const c=before.find(p=>p.aglFt<=g), gd=(r.gates||[]).find(x=>x.aglFt===g);
    if(c&&X(c)>=x0) svg+=`<circle cx="${sx(X(c)).toFixed(1)}" cy="${yy}" r="4.5" class="pf-gdot ${gd&&gd.recorded?(gd.stable?'ok':'bad'):''}"/>`; });
  svg+=`<g ${cp()}><path d="${path(p=>p.aglFt,sa)}L${sx(X(pts[pts.length-1])).toFixed(1)} ${sa(0)}L${sx(x0).toFixed(1)} ${sa(0)}Z" class="pf-area"/><path d="${path(p=>p.aglFt,sa)}" class="pf-alt"/></g>`;
  // 2 — IAS / GS
  // scaled on the approach (to 3 s after touchdown) so the speed trend isn't flattened by the rollout
  const sp=pts.filter(p=>p.t<=3).flatMap(p=>[p.iasKts,p.gsKts]);
  const si=panel(y2,H2,Math.min(...sp)-4,Math.max(...sp)+4,3,'IAS <tspan class="pf-gsl">GS</tspan> KT');
  svg+=`<g ${cp()}><path d="${path(p=>p.gsKts,si)}" class="pf-gs"/><path d="${path(p=>p.iasKts,si)}" class="pf-ias"/></g>`;
  // 3 — vertical speed, −1000 fpm stabilisation limit
  const vsl=pts.map(p=>p.vsFpm), vlo=Math.min(-1100,...vsl), vhi=Math.max(200,...vsl);
  const sv=panel(y3,H3,vlo,vhi,3,'V/S FPM',v=>num(v));
  svg+=`<line x1="${L}" x2="${L+pw}" y1="${sv(-1000).toFixed(1)}" y2="${sv(-1000).toFixed(1)}" class="pf-lim"/><line x1="${L}" x2="${L+pw}" y1="${sv(0).toFixed(1)}" y2="${sv(0).toFixed(1)}" class="pf-zero"/>
    <path d="${path(p=>p.vsFpm,sv)}" class="pf-vs" ${cp()}/>`;
  // 4 — pitch / bank
  const at=pts.flatMap(p=>[p.pitchDeg,p.bankDeg]);
  const st=panel(y4,H4,Math.min(-4,...at),Math.max(8,...at),3,'PITCH <tspan class="pf-bkl">BANK</tspan> °');
  svg+=`<line x1="${L}" x2="${L+pw}" y1="${st(0).toFixed(1)}" y2="${st(0).toFixed(1)}" class="pf-zero"/><g ${cp()}><path d="${path(p=>p.bankDeg,st)}" class="pf-bank"/><path d="${path(p=>p.pitchDeg,st)}" class="pf-pitch"/></g>`;
  // x axis + touchdown line
  const xk=lrNice(x0,x1,axis==='dist'?6:7);
  for(let v=xk.lo;v<=xk.hi+1e-9;v+=xk.step){ if(v<x0-1e-9||v>x1+1e-9) continue;
    svg+=`<text x="${sx(v).toFixed(1)}" y="${H-8}" class="pf-ax" text-anchor="middle">${axis==='dist'?(Math.round(v*100)/100):Math.round(v)}</text>`; }
  if(0>=x0&&0<=x1) svg+=`<line x1="${sx(0).toFixed(1)}" x2="${sx(0).toFixed(1)}" y1="${y1}" y2="${y4+H4}" class="pf-td"/><text x="${(sx(0)+4).toFixed(1)}" y="${y1+24}" class="pf-tdt">TD</text>`;
  svg+=`<line id="lrCur" x1="-9" x2="-9" y1="${y1}" y2="${y4+H4}" class="pf-cur"/><rect x="${L}" y="${y1}" width="${pw}" height="${y4+H4-y1}" class="pf-hit" id="lrHit"/>`;
  el.innerHTML=`<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="Approach profile"><defs>${clip}</defs>${svg}</svg>`;
  const read=$('#lrRead'), cur=$('#lrCur');
  const hint=axis==='dist'?'NM FROM TOUCHDOWN, DRAG TO READ':'SECONDS FROM TOUCHDOWN, DRAG TO READ';
  const show=p=>{ if(!p){ read.textContent=hint; cur.setAttribute('x1',-9); cur.setAttribute('x2',-9); return; }
    const x=sx(X(p)).toFixed(1); cur.setAttribute('x1',x); cur.setAttribute('x2',x);
    read.innerHTML=`${axis==='dist'?`${(p.distNm).toFixed(2)} NM`:`${p.t.toFixed(1)} S`} · <b>${num(p.aglFt)}</b> FT · <b class="g">${num(p.iasKts)}</b> KT · <b>${num(p.vsFpm)}</b> FPM · P ${num(p.pitchDeg,1)}° B ${num(p.bankDeg,1)}° · ${p.gearDown?'GEAR DN':'GEAR UP'} F${num(p.flapsPct)}%`; };
  const hit=$('#lrHit');
  hit.onpointermove=e=>{ const b=hit.getBoundingClientRect(), v=x0+(e.clientX-b.left)/b.width*(x1-x0);
    let best=pts[0]; for(const p of pts) if(Math.abs(X(p)-v)<Math.abs(X(best)-v)) best=p; show(best); };
  hit.onpointerleave=()=>show(null);
  show(null);
}
// Touchdown zone: plan view of the landing runway (threshold left), TDZ 0–3000 ft, aiming
// point at 1000 ft, the touchdown point (lateral offset exaggerated), rollout to 30 kt.
function lrRunwayFor(r){
  const a=lr.apt[(r.airportIcao||'').toUpperCase()]; if(!a||!a.runways) return null;
  const id=String(r.runway||'').toUpperCase().replace(/^RWY\s*/,'').replace(/[^0-9LRC]/g,'');
  return a.runways.find(x=>x.ident===id)||a.runways.find(x=>x.ident.replace(/[LRC]/g,'')===id.replace(/[LRC]/g,'')&&id)||null;
}
function lrDrawRunway(r){
  const el=$('#lrRwy'), rs=$('#lrRs'); if(!el) return;
  const rw=lrRunwayFor(r), td=r.touchdownDistanceFt, roll=r.rolloutFt>10?r.rolloutFt:0;
  const lenFt=rw?rw.lengthM/0.3048:Math.max((td||0)+roll+2500,8000), widFt=rw&&rw.widthM?rw.widthM/0.3048:148;
  $('#lrRwyH').textContent=rw?`RWY ${rw.ident} · ${num(rw.lengthM)} × ${rw.widthM||'—'} M`:(r.runway||'')+' · LENGTH N/A';
  const W=Math.max(320,el.clientWidth), L=14, R=14, pw=W-L-R, cy=62, hh=34, sx=ft=>L+ft/lenFt*pw;
  let s=`<rect x="${L}" y="${cy-hh}" width="${pw}" height="${hh*2}" class="rw-sfc"/>`;
  s+=`<rect x="${sx(0)}" y="${cy-hh}" width="${sx(Math.min(3000,lenFt))-sx(0)}" height="${hh*2}" class="rw-tdz"/>`;
  for(let i=0;i<8;i++){ const y=cy-hh+4+i*(hh*2-8)/8; s+=`<rect x="${sx(0)+3}" y="${y.toFixed(1)}" width="${Math.max(6,sx(150)-sx(0))}" height="${((hh*2-8)/8*0.6).toFixed(1)}" class="rw-mk"/>`; }
  for(let f=400;f<lenFt-200;f+=250) s+=`<rect x="${sx(f).toFixed(1)}" y="${cy-1}" width="${Math.max(3,sx(f+120)-sx(f)).toFixed(1)}" height="2" class="rw-cl"/>`;
  [500,1500,2000,2500,3000].forEach(f=>{ if(f>=lenFt-600) return; [-1,1].forEach(sd=>s+=`<rect x="${sx(f).toFixed(1)}" y="${(cy+sd*hh*0.55-2).toFixed(1)}" width="${Math.max(4,sx(f+75)-sx(f)).toFixed(1)}" height="4" class="rw-mk"/>`); });
  if(lenFt>2000) [-1,1].forEach(sd=>s+=`<rect x="${sx(1000).toFixed(1)}" y="${(cy+sd*hh*0.5-(sd<0?7:0)).toFixed(1)}" width="${Math.max(8,sx(1150)-sx(1000)).toFixed(1)}" height="7" class="rw-aim"/>`);
  const step=lenFt>9000?2000:1000;
  for(let f=0;f<=lenFt+1;f+=step){ const x=sx(f).toFixed(1);
    s+=`<line x1="${x}" x2="${x}" y1="${cy+hh+30}" y2="${cy+hh+35}" class="rw-tick"/><text x="${x}" y="${cy+hh+46}" class="rw-t dim" text-anchor="${f?'middle':'start'}">${f?num(f/1000)+'K':'0'}</text>`; }
  s+=`<line x1="${L}" x2="${L+pw}" y1="${cy+hh+30}" y2="${cy+hh+30}" class="rw-tick"/><text x="${L+pw}" y="${cy+hh+46}" class="rw-t dim" text-anchor="end">FT FROM THR</text>`;
  s+=`<text x="${sx(0)+4}" y="${cy-hh-8}" class="rw-t">THR ${rw?esc(rw.ident):''}</text><text x="${sx(Math.min(3000,lenFt))}" y="${cy-hh-8}" class="rw-t" text-anchor="end">TDZ 3000 FT</text>`;
  let stats='';
  if(td!=null){
    const dev=r.centerlineDeviationFt||0, dy=Math.max(-hh+4,Math.min(hh-4,dev/(widFt/2)*hh));
    const tx=sx(td), stop=Math.min(lenFt,td+roll);
    if(roll) s+=`<line x1="${tx.toFixed(1)}" y1="${(cy+dy).toFixed(1)}" x2="${sx(stop).toFixed(1)}" y2="${cy}" class="rw-roll"/><path d="M${sx(stop).toFixed(1)} ${cy-6}v12" class="rw-stop"/>
      <text x="${sx(stop).toFixed(1)}" y="${cy+hh+15}" class="rw-t" text-anchor="middle">30 KT</text>`;
    const cls=td>3000||td<200?'bad':td>2500?'warn':'ok';
    s+=`<circle cx="${tx.toFixed(1)}" cy="${(cy+dy).toFixed(1)}" r="7" class="rw-td ${cls}"/><text x="${tx.toFixed(1)}" y="${cy+hh+15}" class="rw-t ${cls}" text-anchor="middle">TD${r.touchdownDistanceEstimated?' (EST)':''}</text>`;
    const rem=rw?lenFt-td-roll:null;
    stats=[['TD PAST THR',ftM(td)+(r.touchdownDistanceEstimated?' <em>est. from 50 ft</em>':''),cls==='ok'?'':cls],
      ['CENTRELINE',r.centerlineDeviationFt?`${num(Math.abs(dev))} ft ${dev>0?'R':'L'}`:'—',Math.abs(dev)>=60?'bad':Math.abs(dev)>=30?'warn':''],
      ['ROLLOUT',roll?ftM(roll):'—',''],['REMAINING',rem!=null&&roll?ftM(Math.max(0,rem)):'—',rem!=null&&roll&&rem<1000?'warn':''],
      ['DECEL',r.decelerationKtPerSec?`${num(r.decelerationKtPerSec,1)} kt/s`:'—','']]
      .map(([l,v,c])=>`<div class="${c}"><label>${l}</label><b class="mono">${v}</b></div>`).join('');
  } else stats=`<div class="lr-none wide">Touchdown point not available. The runway isn't in the tracker's database and no approach trace was recorded.</div>`;
  el.innerHTML=`<svg width="${W}" height="${cy+hh+52}" viewBox="0 0 ${W} ${cy+hh+52}" role="img" aria-label="Touchdown zone">${s}</svg>`;
  rs.innerHTML=stats;
}

// ── SCRATCHPAD APP ──
// The pilot's notepad, in the ForeFlight Scratchpads / airline-EFB idiom: a stack of pages,
// each on a template (CRAFT clearance, departure & arrival ATIS, takeoff and approach briefs,
// holding, frequencies, taxi route, or blank / lined / grid / typed), written on with ink.
//   Ink      — pressure-sensitive with Apple Pencil / a stylus, speed-sensitive with a finger
//              or mouse; smoothed through quadratic midpoints; incremental live layer so it
//              keeps up at 120 Hz; highlighter that never darkens where it overlaps itself.
//   Tools    — pen · highlighter · stroke eraser · text stamps (UTC time, squawk, QNH…),
//              4 colours, 3 sizes, undo / redo (Ctrl+Z / Ctrl+Shift+Z), per-page history.
//   Paper    — Night (cockpit dark, default) or Day; ink colours adapt, so a page written at
//              night reads correctly on day paper.
//   Hints    — faint cyan values the EFB already knows (OFP runways/SID/STAR, squawk, the
//              METAR, V-speeds from Departure Perf…) printed in each template box; toggleable.
//   Pencil   — once a pen is detected, touches no longer ink (palm rejection).
// Pages live on this device only (localStorage, per-viewer convenience — like a real EFB's
// notes); coordinates are in units of a 1000-wide page, so rotation/resizes never misalign ink.
const SP_KEY='efbScratchPages', SP_PREF='efbScratchTool';
const SP_PAL={
  day:  {paper:'#fbfaf5',line:'#d9dbe0',label:'#6b7280',acc:'#1f5fbf',hint:'#0f8fb8',dot:'#cfd2d8',
         ink:'#15171c',blue:'#1f5fbf',red:'#cf2e2e',green:'#15814a', hl:{ink:'#ffd83d',blue:'#64d2ff',red:'#ff8fb3',green:'#8ee07a'}},
  night:{paper:'#17181b',line:'#33363c',label:'#8d9199',acc:'#4D9BEC',hint:'#39cff2',dot:'#2e3136',
         ink:'#eef0f3',blue:'#6db3ff',red:'#ff6b6b',green:'#48ec82', hl:{ink:'#ffd23f',blue:'#39cff2',red:'#ff6fa3',green:'#48ec82'}},
};
const SP_SIZES=[2.2,3.8,6.5], SP_HL=[16,24,34], SP_ER=[10,18,30], SP_TXT=[16,21,28];
const SP_FONT='ui-monospace,"SF Mono","Cascadia Code",Consolas,monospace';
const SP_TPL={
  craft:{name:'CRAFT Clearance', d:'Clearance limit, route, altitude, frequency, transponder.'},
  atis:{name:'ATIS', d:'Departure and arrival ATIS side by side.'},
  dep:{name:'Takeoff Brief', d:'Runway, SID, V-speeds, flap and trim, emergency turn, taxi.'},
  arr:{name:'Approach Brief', d:'STAR, approach, minima, missed approach, VAPP, autobrake.'},
  hold:{name:'Holding', d:'Fix, inbound course, turns, leg, EFC, with a pattern to sketch on.'},
  freq:{name:'Frequencies', d:'Station and frequency log from gate to gate.'},
  taxi:{name:'Taxi Route', d:'From / to, route and hold-short points.'},
  blank:{name:'Blank', d:'Dotted paper.'},
  lined:{name:'Lined', d:'Ruled lines for free notes.'},
  grid:{name:'Grid', d:'Squared paper for sketches and diagrams.'},
  note:{name:'Typed Note', d:'Keyboard notes, monospaced.'},
};
let sp=null, spHist={}, spTool='pen', spColor='ink', spSize=1, spPenSeen=false;
try{ const p=JSON.parse(localStorage.getItem(SP_PREF)||'{}'); spTool=p.tool||'pen'; spColor=p.color||'ink'; spSize=p.size??1; }catch{}

// the flight pack a new flight starts with, like an airline's pre-printed nav log / notepad set
const SP_PACK=['craft','atis','dep','arr','blank'];
if(typeof CanvasRenderingContext2D!=='undefined'&&!CanvasRenderingContext2D.prototype.roundRect)
  CanvasRenderingContext2D.prototype.roundRect=function(x,y,w,h,r){ this.moveTo(x+r,y); this.arcTo(x+w,y,x+w,y+h,r); this.arcTo(x+w,y+h,x,y+h,r); this.arcTo(x,y+h,x,y,r); this.arcTo(x,y,x+w,y,r); this.closePath(); };
function spNewPage(tpl){ return {id:'p'+Date.now().toString(36)+Math.random().toString(36).slice(2,6), tpl, created:new Date().toISOString(), items:[], text:''}; }
function spLoad(){
  if(sp) return sp;
  try{ sp=JSON.parse(localStorage.getItem(SP_KEY)||'null'); }catch{ sp=null; }
  if(!sp||!Array.isArray(sp.pages)||!sp.pages.length){
    sp={v:1,paper:'night',hints:true,pages:SP_PACK.map(spNewPage),cur:null};
    let old=''; try{ old=localStorage.getItem('efbScratchText')||''; }catch{}
    if(old.trim()){ const n=spNewPage('note'); n.text=old; sp.pages.push(n); }   // keep notes from the old scratchpad
    sp.cur=sp.pages[0].id;
  }
  if(!sp.pages.some(p=>p.id===sp.cur)) sp.cur=sp.pages[0].id;
  return sp;
}
let spSaveT=null;
function spSave(now){ clearTimeout(spSaveT);
  const f=()=>{ try{ localStorage.setItem(SP_KEY,JSON.stringify(sp)); }catch{} spThumb(spPage()); };
  if(now) f(); else spSaveT=setTimeout(f,450); }
function spPrefs(){ try{ localStorage.setItem(SP_PREF,JSON.stringify({tool:spTool,color:spColor,size:spSize})); }catch{} }
const spPage=()=>sp&&sp.pages.find(p=>p.id===sp.cur);
const spH=id=>spHist[id]||(spHist[id]={undo:[],redo:[]});
const spZ=iso=>{ try{ return new Date(iso).toISOString().substr(11,5).replace(':','')+'Z'; }catch{ return ''; } };
const spTitle=p=>`${(SP_TPL[p.tpl]||SP_TPL.blank).name} · ${spZ(p.created)}`;

// ── values the EFB already knows, for the template hints and text stamps ──
function spKnown(){
  const P=plan||{}, S=state||{}, d=dep&&dep.result, a=arr&&arr.result;
  const md=parseMetar(P.departureMetar||''), ma=parseMetar(P.arrivalMetar||'');
  const wind=m=>m.windKts!=null?`${String(m.windDir||0).padStart(3,'0')}/${m.windKts}`:'';
  const sq=S.squawk&&!['2000','1200','7000','0000'].includes(String(S.squawk))?String(S.squawk):'';
  return {
    dep:P.departureIcao||S.departureIcao||'', arr:P.arrivalIcao||S.arrivalIcao||'', altn:P.alternateIcao||'',
    depRwy:P.departureRunway||'', arrRwy:P.arrivalRunway||'', sid:P.sid||'', star:P.star||'',
    cruise:P.cruiseAltitudeFt?`FL${Math.round(P.cruiseAltitudeFt/100)}`:'', ta:P.departureTransAltFt?String(P.departureTransAltFt):'',
    squawk:sq, callsign:S.callsign||P.flightNumber||'',
    depWind:wind(md), depQnh:md.qnh?String(md.qnh):'', depTemp:md.tempC!=null?`${md.tempC}/${md.dewC}`:'',
    arrWind:wind(ma), arrQnh:ma.qnh?String(ma.qnh):'', arrTemp:ma.tempC!=null?`${ma.tempC}/${ma.dewC}`:'',
    vspeeds:d?`${d.v1} / ${d.vr} / ${d.v2}`:'', flaps:d?flapShort(d.flapSetting):'',
    trim:d?(d.ths||(d.stabTrimUnits?d.stabTrimUnits.toFixed(2):'')):'', flex:d?(d.flexTempC!=null?`FLEX ${d.flexTempC}°`:'TOGA'):'',
    vapp:a?`${a.vref} / ${a.vapp}`:'', autobrake:a?String(a.autobrake||''):'', ldgFlap:a?flapShort(a.flapSetting):'',
    ldgFuel:P.landingFuelLbs?`${bigFromKg(P.landingFuelLbs/LB_KG).toFixed(1)} ${WU()==='LB'?'K LB':'T'} PLAN`:'',
  };
}

// ── templates, drawn in page units (1000 wide) ──
function spTemplate(g,tpl,s,hu,pal,showHints){
  const K=showHints?spKnown():{};
  const U=v=>v*s;
  const line=(x1,y1,x2,y2,c=pal.line,w=1.2)=>{ g.strokeStyle=c; g.lineWidth=w; g.beginPath(); g.moveTo(U(x1),U(y1)); g.lineTo(U(x2),U(y2)); g.stroke(); };
  const text=(t,x,y,z,c,wt=600,al='left',fam='var')=>{ if(!t) return; g.fillStyle=c; g.textAlign=al;
    g.font=`${wt} ${U(z)}px ${fam==='mono'?SP_FONT:'-apple-system,"Segoe UI",Roboto,sans-serif'}`; g.fillText(String(t),U(x),U(y)); };
  const box=(x,y,w,h,label,hint)=>{
    g.strokeStyle=pal.line; g.lineWidth=1.3; g.beginPath(); g.roundRect(U(x),U(y),U(w),U(h),U(6)); g.stroke();
    text(label,x+10,y+19,12.5,pal.label,700);
    if(hint){ g.globalAlpha=.85; text(hint,x+w-10,y+h-10,14,pal.hint,600,'right','mono'); g.globalAlpha=1; }
  };
  const header=(t,sub)=>{ text(t,24,40,15,pal.acc,800); if(sub) text(sub,976,40,13,pal.label,600,'right','mono'); line(24,52,976,52,pal.line,1.6); };
  const dots=(y0=0,step=25)=>{ g.fillStyle=pal.dot; for(let y=y0+step/2;y<hu;y+=step) for(let x=step/2;x<1000;x+=step){ g.beginPath(); g.arc(U(x),U(y),Math.max(.9,U(1.3)),0,7); g.fill(); } };
  const lines=(y0,step=38)=>{ for(let y=y0;y<hu-8;y+=step) line(24,y,976,y); };
  const grid=(step=25)=>{ for(let x=0;x<=1000;x+=step) line(x,0,x,hu,pal.dot,.8); for(let y=0;y<=hu;y+=step) line(0,y,1000,y,pal.dot,.8); };
  switch(tpl){
    case 'blank': dots(); break;
    case 'lined': line(90,0,90,hu,pal.red,1); lines(60); break;
    case 'grid': grid(); break;
    case 'note': lines(60,32); break;
    case 'craft': {
      header('CLEARANCE / CRAFT',[K.callsign,K.dep&&K.arr?`${K.dep} → ${K.arr}`:''].filter(Boolean).join('  '));
      const rows=[['C','CLEARANCE LIMIT',K.arr],['R','ROUTE / SID / RUNWAY',[K.sid,K.depRwy&&'RWY '+K.depRwy].filter(Boolean).join('  ')],
        ['A','ALTITUDE / INITIAL / EXPECT',K.cruise&&'EXPECT '+K.cruise],['F','DEPARTURE FREQUENCY',''],['T','TRANSPONDER',K.squawk]];
      let y=66; const h=Math.min(116,Math.max(84,(hu-66-150)/5));
      rows.forEach(([L,lab,hint])=>{ box(24,y,952,h-10,'',hint); text(L,62,y+h/2+10,46,pal.acc,800,'center');
        line(100,y+8,100,y+h-18); text(lab,114,y+22,12.5,pal.label,700); y+=h; });
      text('NOTES',24,y+26,12.5,pal.label,700); lines(y+60); break; }
    case 'atis': {
      header('ATIS / INFORMATION','DEPARTURE / ARRIVAL');
      const col=(x,title,icao,wind,qnh,temp)=>{
        text(title,x,86,13,pal.acc,800); text(icao,x+460,86,14,pal.hint,700,'right','mono');
        g.strokeStyle=pal.acc; g.lineWidth=2; g.beginPath(); g.roundRect(U(x),U(100),U(110),U(110),U(10)); g.stroke();
        text('INFO',x+55,122,11,pal.label,700,'center');
        box(x+124,100,336,50,'TIME (Z)',''); box(x+124,160,336,50,'RUNWAY / APPROACH',title.startsWith('DEP')?K.depRwy:K.arrRwy);
        const r=[['WIND',wind],['VISIBILITY',''],['WEATHER / CLOUD',''],['TEMP / DEW',temp],['QNH',qnh],['TRANSITION LEVEL','']];
        let y=222; r.forEach(([l,h])=>{ box(x,y,460,56,l,h); y+=64; });
        box(x,y,460,Math.max(90,hu-y-20),'REMARKS / NOTAM / BIRD / RWY STATE',''); };
      col(24,'DEPARTURE',K.dep,K.depWind,K.depQnh,K.depTemp); col(516,'ARRIVAL',K.arr,K.arrWind,K.arrQnh,K.arrTemp); break; }
    case 'dep': {
      header('TAKEOFF BRIEF',[K.dep,K.depRwy&&'RWY '+K.depRwy].filter(Boolean).join('  '));
      const c3=(y,cells)=>cells.forEach((c,i)=>box(24+i*320,y,312,78,c[0],c[1]));
      c3(66,[['RUNWAY / INTERSECTION',K.depRwy],['SID',K.sid],['INITIAL ALTITUDE','']]);
      c3(152,[['TRANSITION ALT',K.ta],['SQUAWK',K.squawk],['DEPARTURE FREQ','']]);
      c3(238,[['V1 / VR / V2',K.vspeeds],['FLAPS / TRIM',[K.flaps,K.trim].filter(Boolean).join(' / ')],['THRUST',K.flex]]);
      let y=324; const rest=Math.max(3*80,hu-y-16), h=rest/3;
      [['ENGINE FAILURE / EMERGENCY TURN / RETURN PLAN',''],['TAXI ROUTE / HOT SPOTS',''],['THREATS / WEATHER / MEL','']]
        .forEach(([l,hn])=>{ box(24,y,952,h-8,l,hn); y+=h; }); break; }
    case 'arr': {
      header('APPROACH BRIEF',[K.arr,K.arrRwy&&'RWY '+K.arrRwy].filter(Boolean).join('  '));
      const c3=(y,cells)=>cells.forEach((c,i)=>box(24+i*320,y,312,78,c[0],c[1]));
      c3(66,[['STAR / TRANSITION',K.star],['APPROACH / RUNWAY',K.arrRwy&&'RWY '+K.arrRwy],['MINIMA  DA / MDA','']]);
      c3(152,[['MISSED APPROACH ALT',''],['TRANSITION LEVEL',''],['LANDING FLAP',K.ldgFlap]]);
      c3(238,[['VREF / VAPP',K.vapp],['AUTOBRAKE',K.autobrake],['FUEL AT DEST / ALTN',K.ldgFuel]]);
      let y=324; const rest=Math.max(3*80,hu-y-16), h=rest/3;
      [['GO-AROUND / DIVERSION PLAN',K.altn&&'ALTN '+K.altn],['TAXI-IN / STAND / EXIT',''],['THREATS / WEATHER / NOTAM','']].forEach(([l,hn])=>{ box(24,y,952,h-8,l,hn); y+=h; }); break; }
    case 'hold': {
      header('HOLDING','ICAO: ≤ FL140 230 KT · 1 MIN');
      const cells=[['FIX',''],['INBOUND COURSE',''],['TURNS   L  /  R',''],['LEG  MIN / NM',''],['ALTITUDE',''],['EFC (Z)','']];
      cells.forEach((c,i)=>box(24+(i%3)*320,66+Math.floor(i/3)*86,312,78,c[0],c[1]));
      // a standard right-hand racetrack to sketch the entry on
      const cx=500, cy=Math.min(430,66+172+Math.max(140,(hu-66-172)/2)), rw=210, rr=70;
      g.setLineDash([U(10),U(8)]); g.strokeStyle=pal.label; g.globalAlpha=.55; g.lineWidth=U(2.2);
      g.beginPath(); g.moveTo(U(cx-rw),U(cy-rr)); g.lineTo(U(cx+rw),U(cy-rr));
      g.arc(U(cx+rw),U(cy),U(rr),-Math.PI/2,Math.PI/2); g.lineTo(U(cx-rw),U(cy+rr));
      g.arc(U(cx-rw),U(cy),U(rr),Math.PI/2,Math.PI*1.5); g.stroke(); g.setLineDash([]); g.globalAlpha=1;
      // inbound leg arrow to the fix
      g.strokeStyle=pal.acc; g.fillStyle=pal.acc; g.lineWidth=U(2.4);
      g.beginPath(); g.moveTo(U(cx-rw),U(cy+rr)); g.lineTo(U(cx+rw-18),U(cy+rr)); g.stroke();
      g.beginPath(); g.moveTo(U(cx+rw-4),U(cy+rr)); g.lineTo(U(cx+rw-26),U(cy+rr-10)); g.lineTo(U(cx+rw-26),U(cy+rr+10)); g.closePath(); g.fill();
      g.beginPath(); g.moveTo(U(cx+rw),U(cy+rr-13)); g.lineTo(U(cx+rw+12),U(cy+rr+9)); g.lineTo(U(cx+rw-12),U(cy+rr+9)); g.closePath(); g.stroke();
      text('FIX',cx+rw,cy+rr+34,12,pal.acc,800,'center'); text('INBOUND',cx,cy+rr-12,11.5,pal.label,700,'center');
      text('OUTBOUND',cx,cy-rr+22,11.5,pal.label,700,'center');
      text('ENTRY:   DIRECT   ·   PARALLEL   ·   TEARDROP',500,Math.min(hu-24,cy+rr+84),13,pal.label,700,'center'); break; }
    case 'freq': {
      header('FREQUENCIES','STATION / FREQ / NOTES');
      const rows=[['DEP ATIS',K.dep],['DELIVERY',''],['GROUND',''],['TOWER',''],['DEPARTURE',''],['CENTRE',''],['CENTRE',''],['APPROACH',''],['ARR ATIS',K.arr],['TOWER',''],['GROUND',''],['UNICOM','122.800'],['GUARD','121.500']];
      const h=Math.max(38,Math.min(52,(hu-80)/rows.length)); let y=66;
      line(250,58,250,Math.min(hu,y+rows.length*h)); line(500,58,500,Math.min(hu,y+rows.length*h));
      rows.forEach(([n,hn])=>{ text(n,34,y+h/2+6,13,pal.label,700); if(hn) { g.globalAlpha=.85; text(hn,490,y+h/2+6,14,pal.hint,600,'right','mono'); g.globalAlpha=1; }
        y+=h; line(24,y,976,y); }); break; }
    case 'taxi': {
      header('TAXI ROUTE',[K.dep,K.depRwy&&'RWY '+K.depRwy].filter(Boolean).join('  '));
      box(24,66,470,78,'FROM / STAND / GATE',''); box(506,66,470,78,'TO / RUNWAY / HOLDING POINT',K.depRwy&&'RWY '+K.depRwy);
      const h=Math.max(260,hu-152-170); box(24,152,952,h,'ROUTE',''); box(24,160+h,952,Math.max(100,hu-168-h),'HOLD SHORT / HOT SPOTS / CROSSINGS',''); break; }
  }
}

// ── ink rendering ──
function spInkColor(it,pal){ return it.hl?(pal.hl[it.c]||pal.hl.ink):(pal[it.c]||pal.ink); }
function spPath(g,P,s){ g.moveTo(P[0][0]*s,P[0][1]*s);
  for(let i=1;i<P.length;i++){ const a=P[i-1], b=P[i]; g.quadraticCurveTo(a[0]*s,a[1]*s,(a[0]+b[0])/2*s,(a[1]+b[1])/2*s); }
  const L=P[P.length-1]; g.lineTo(L[0]*s,L[1]*s); }
function spDrawItem(g,it,s,pal){
  g.save();
  if(it.k==='t'){ g.fillStyle=pal[it.c]||pal.ink; g.textBaseline='alphabetic'; g.font=`600 ${it.z*s}px ${SP_FONT}`; g.fillText(it.v,it.x*s,it.y*s); g.restore(); return; }
  const P=it.p; if(!P||!P.length){ g.restore(); return; }
  const col=spInkColor(it,pal); g.lineCap='round'; g.lineJoin='round';
  if(it.hl){ g.globalAlpha=sp.paper==='day'?.4:.62; g.globalCompositeOperation=sp.paper==='day'?'multiply':'screen';
    g.strokeStyle=col; g.lineWidth=it.w*s; g.beginPath(); spPath(g,P,s); if(P.length===1) g.lineTo(P[0][0]*s+.1,P[0][1]*s); g.stroke(); g.restore(); return; }
  g.strokeStyle=col; g.fillStyle=col;
  if(P.length===1){ g.beginPath(); g.arc(P[0][0]*s,P[0][1]*s,P[0][2]*s/2,0,7); g.fill(); g.restore(); return; }
  let x0=P[0][0]*s, y0=P[0][1]*s;
  for(let i=1;i<P.length;i++){ const a=P[i-1], b=P[i], mx=(a[0]+b[0])/2*s, my=(a[1]+b[1])/2*s;
    g.beginPath(); g.moveTo(x0,y0); g.quadraticCurveTo(a[0]*s,a[1]*s,mx,my); g.lineWidth=Math.max(.6,(a[2]+b[2])/2*s); g.stroke(); x0=mx; y0=my; }
  const L=P[P.length-1]; g.beginPath(); g.moveTo(x0,y0); g.lineTo(L[0]*s,L[1]*s); g.lineWidth=Math.max(.6,L[2]*s); g.stroke();
  g.restore();
}
function spBBox(it){ if(it._bb) return it._bb;
  if(it.k==='t') return it._bb=[it.x,it.y-it.z,it.x+it.v.length*it.z*.62,it.y+it.z*.25];
  let a=1e9,b=1e9,c=-1e9,d=-1e9; for(const p of it.p){ a=Math.min(a,p[0]); b=Math.min(b,p[1]); c=Math.max(c,p[0]); d=Math.max(d,p[1]); }
  const m=(it.w||4)/2; return it._bb=[a-m,b-m,c+m,d+m]; }
function spHit(it,x,y,r){ const bb=spBBox(it); if(x<bb[0]-r||x>bb[2]+r||y<bb[1]-r||y>bb[3]+r) return false;
  if(it.k==='t') return true;
  const P=it.p, rr=r+(it.w||4)/2; if(P.length===1) return Math.hypot(P[0][0]-x,P[0][1]-y)<=rr;
  for(let i=1;i<P.length;i++){ const [x1,y1]=P[i-1],[x2,y2]=P[i], dx=x2-x1, dy=y2-y1, l=dx*dx+dy*dy;
    const t=l?Math.max(0,Math.min(1,((x-x1)*dx+(y-y1)*dy)/l)):0; if(Math.hypot(x1+t*dx-x,y1+t*dy-y)<=rr) return true; }
  return false; }
const spJson=it=>{ const {_bb,...o}=it; return o; };

// thumbnail of a page (template + ink) on its card
function spThumb(p){ if(!p) return; const cv=document.querySelector(`#spPages canvas[data-p="${p.id}"]`); if(!cv) return;
  const W=cv.clientWidth||120, H=cv.clientHeight||80, dpr=window.devicePixelRatio||1;
  cv.width=W*dpr; cv.height=H*dpr; const g=cv.getContext('2d'); g.scale(dpr,dpr);
  const pal=SP_PAL[sp.paper], s=W/1000; g.fillStyle=pal.paper; g.fillRect(0,0,W,H);
  if(p.tpl!=='blank'&&p.tpl!=='grid') spTemplate(g,p.tpl,s,H/s,pal,false);
  p.items.forEach(it=>spDrawItem(g,it,s,pal));
  if(p.tpl==='note'&&p.text){ g.fillStyle=pal.ink; g.font=`${Math.max(5,14*s*1.6)}px ${SP_FONT}`;
    p.text.split('\n').slice(0,12).forEach((l,i)=>g.fillText(l.slice(0,60),24*s,(60+i*32)*s-4)); }
}

function renderScratch(body){
  body=body||$('#appBody'); body.classList.add('nopad'); spLoad();
  const toolBtn=(k,label,icon,key)=>`<button class="sp-tb ${spTool===k?'on':''}" data-tool="${k}" title="${label} (${key})" aria-pressed="${spTool===k}"><svg ${SVG_ATTRS}>${icon}</svg></button>`;
  body.innerHTML=`
  <div class="sp sp-${sp.paper}">
    <header class="sp-top">
      <div class="sp-id"><b>SCRATCHPAD</b><span id="spTitle"></span></div>
      <div class="sp-tools" role="toolbar" aria-label="Ink tools">
        <div class="sp-grp">
          ${toolBtn('pen','Pen','<path d="M4 20l4.5-1.2L19 8.3a2 2 0 0 0 0-2.8l-.5-.5a2 2 0 0 0-2.8 0L5.2 15.5z"/><path d="M14.5 6.5l3 3"/>','P')}
          ${toolBtn('hl','Highlighter','<path d="M9 17l-4 4h6l1.5-1.5"/><path d="M8 14l7-9.5a1.8 1.8 0 0 1 2.6-.2l1.1 1.1a1.8 1.8 0 0 1-.2 2.6L9 15z"/><path d="M8 14l2 2"/>','H')}
          ${toolBtn('er','Eraser','<path d="M4 16.5L13.5 7a2 2 0 0 1 2.8 0l2.7 2.7a2 2 0 0 1 0 2.8L12 19.5H7z"/><path d="M9 11.5l5.5 5.5"/><path d="M12 19.5h8"/>','E')}
          ${toolBtn('tx','Text stamp','<path d="M5 6V4.5h14V6"/><path d="M12 4.5v15"/><path d="M9 19.5h6"/>','T')}
        </div>
        <div class="sp-grp sp-cols" id="spCols">${['ink','blue','red','green'].map(c=>`<button class="sp-sw ${spColor===c?'on':''}" data-col="${c}" title="${c==='ink'?'Ink':c[0].toUpperCase()+c.slice(1)}"><i></i></button>`).join('')}</div>
        <div class="sp-grp" id="spSizes">${[0,1,2].map(i=>`<button class="sp-sz ${spSize===i?'on':''}" data-size="${i}" title="${['Fine','Medium','Bold'][i]}"><i style="--d:${[4,7,11][i]}px"></i></button>`).join('')}</div>
      </div>
      <div class="sp-acts">
        <span class="sp-pencil ${spPenSeen?'':'hidden'}" id="spPencil" title="Pencil detected: finger touches won't ink">PENCIL</span>
        <button class="sp-ab" id="spUndo" title="Undo (Ctrl+Z)"><svg ${SVG_ATTRS}><path d="M9 14L4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/></svg></button>
        <button class="sp-ab" id="spRedo" title="Redo (Ctrl+Shift+Z)"><svg ${SVG_ATTRS}><path d="M15 14l5-5-5-5"/><path d="M20 9H9.5a5.5 5.5 0 0 0 0 11H13"/></svg></button>
        <button class="sp-ab ${sp.hints?'on':''}" id="spHints" title="EFB hints: values from the OFP and perf apps">HINTS</button>
        <button class="sp-ab" id="spPaper" title="Day / night paper">${sp.paper==='night'?'NIGHT':'DAY'}</button>
        <button class="sp-ab warn" id="spClear" title="Clear this page">CLEAR</button>
      </div>
    </header>
    <div class="sp-body">
      <aside class="sp-pages" id="spPages" aria-label="Pages"></aside>
      <section class="sp-stage" id="spStage">
        <canvas id="spBg"></canvas><canvas id="spInk"></canvas><canvas id="spLive"></canvas>
        <textarea id="spNote" class="sp-note hidden" spellcheck="false" placeholder="Type your notes…"></textarea>
        <div class="sp-tx hidden" id="spTx"><input id="spTxIn" autocomplete="off" spellcheck="false" placeholder="Type, then Enter"><div class="sp-chips" id="spChips"></div></div>
        <div class="sp-picker hidden" id="spPicker"></div>
      </section>
    </div>
  </div>`;
  const stage=$('#spStage'), bg=$('#spBg'), ink=$('#spInk'), live=$('#spLive'), note=$('#spNote');
  let s=1, W=0, H=0, dpr=1, gB, gI, gL;

  function layout(){
    // CSS px from clientWidth (the interface-size zoom doesn't change it); the backing store is
    // sized for the rendered pixels so ink stays sharp at any zoom
    W=Math.max(200,stage.clientWidth); H=Math.max(200,stage.clientHeight); dpr=(window.devicePixelRatio||1)*uiZ(); s=W/1000;
    [bg,ink,live].forEach(c=>{ c.width=Math.round(W*dpr); c.height=Math.round(H*dpr); c.style.width=W+'px'; c.style.height=H+'px'; });
    gB=bg.getContext('2d'); gI=ink.getContext('2d'); gL=live.getContext('2d');
    [gB,gI,gL].forEach(g=>g.setTransform(dpr,0,0,dpr,0,0));
    paintAll();
  }
  function paintBg(){ const p=spPage(), pal=SP_PAL[sp.paper]; gB.clearRect(0,0,W,H); gB.fillStyle=pal.paper; gB.fillRect(0,0,W,H); spTemplate(gB,p.tpl,s,H/s,pal,sp.hints); }
  function paintInk(){ const p=spPage(), pal=SP_PAL[sp.paper]; gI.clearRect(0,0,W,H); p.items.forEach(it=>spDrawItem(gI,it,s,pal)); }
  function paintAll(){ const p=spPage(); paintBg(); paintInk(); gL.clearRect(0,0,W,H);
    const isNote=p.tpl==='note'; note.classList.toggle('hidden',!isNote); if(isNote){ note.value=p.text||''; note.style.setProperty('--lh',(32*s)+'px'); note.style.setProperty('--top',(40*s)+'px'); note.style.setProperty('--fs',Math.max(14,17*s)+'px'); }
    $('#spTitle').textContent=spTitle(p); histBtns(); }
  function histBtns(){ const h=spH(sp.cur); $('#spUndo').disabled=!h.undo.length; $('#spRedo').disabled=!h.redo.length; }
  function push(op){ const h=spH(sp.cur); h.undo.push(op); if(h.undo.length>200) h.undo.shift(); h.redo=[]; histBtns(); spSave(); }
  function apply(op,rev){ const p=spPage();
    if(op.t==='add'){ if(rev) p.items=p.items.filter(i=>i!==op.it); else p.items.push(op.it); }
    else if(op.t==='del'||op.t==='clr'){ if(rev){ op.its.forEach(([it,ix])=>p.items.splice(Math.min(ix,p.items.length),0,it)); } else { const set=new Set(op.its.map(x=>x[0])); p.items=p.items.filter(i=>!set.has(i)); } } }
  function undo(){ const h=spH(sp.cur), op=h.undo.pop(); if(!op) return; apply(op,true); h.redo.push(op); paintInk(); histBtns(); spSave(); }
  function redo(){ const h=spH(sp.cur), op=h.redo.pop(); if(!op) return; apply(op,false); h.undo.push(op); paintInk(); histBtns(); spSave(); }

  // ── pages sidebar ──
  function pagesPaint(){
    const el=$('#spPages');
    el.innerHTML=sp.pages.map((p,i)=>`<div class="sp-pc ${p.id===sp.cur?'on':''}" data-p="${p.id}" role="button" tabindex="0" aria-label="${esc(spTitle(p))}">
        <canvas data-p="${p.id}"></canvas>
        <span class="sp-pn"><b>${esc((SP_TPL[p.tpl]||SP_TPL.blank).name)}</b><small class="mono">${i+1} · ${spZ(p.created)}</small></span>
        ${sp.pages.length>1?`<button class="sp-pdel" data-del="${p.id}" title="Delete page" aria-label="Delete page">×</button>`:''}</div>`).join('')
      +`<button class="sp-new" id="spNew"><span>+</span>New page</button>`
      +`<button class="sp-nf" id="spNf" title="Replace every page with a fresh flight pack">NEW FLIGHT</button>`;
    sp.pages.forEach(spThumb);
    $$('#spPages .sp-pc').forEach(c=>{ const go=()=>{ if(c.dataset.p===sp.cur) return; commitTx(); sp.cur=c.dataset.p; spSave(); pagesPaint(); paintAll(); };
      c.onclick=e=>{ if(e.target.closest('[data-del]')) return; go(); }; c.onkeydown=e=>{ if(e.key==='Enter'||e.key===' '){ e.preventDefault(); go(); } }; });
    $$('#spPages [data-del]').forEach(b=>b.onclick=e=>{ e.stopPropagation();
      if(b.dataset.armed!=='1'){ b.dataset.armed='1'; b.textContent='DELETE?'; b.classList.add('armed'); setTimeout(()=>{ if(b.isConnected){ b.dataset.armed=''; b.textContent='×'; b.classList.remove('armed'); } },2500); return; }
      const ix=sp.pages.findIndex(p=>p.id===b.dataset.del); sp.pages.splice(ix,1); delete spHist[b.dataset.del];
      if(sp.cur===b.dataset.del) sp.cur=sp.pages[Math.max(0,ix-1)].id; spSave(true); pagesPaint(); paintAll(); });
    $('#spNew').onclick=()=>picker(true);
    const nf=$('#spNf');
    nf.onclick=()=>{ if(nf.dataset.armed!=='1'){ nf.dataset.armed='1'; nf.textContent='CLEAR ALL PAGES?'; nf.classList.add('armed');
        setTimeout(()=>{ if(nf.isConnected){ nf.dataset.armed=''; nf.textContent='NEW FLIGHT'; nf.classList.remove('armed'); } },2500); return; }
      commitTx(); sp.pages=SP_PACK.map(spNewPage); sp.cur=sp.pages[0].id; spHist={}; spSave(true); pagesPaint(); paintAll(); };
  }
  function picker(open){ const pk=$('#spPicker'); pk.classList.toggle('hidden',!open); if(!open) return;
    pk.innerHTML=`<div class="sp-pk"><header><b>New page</b><button class="sp-ab" id="spPkX">CLOSE</button></header>
      <div class="sp-pkg">${Object.entries(SP_TPL).map(([k,t])=>`<button data-tpl="${k}"><canvas data-tpl="${k}"></canvas><b>${esc(t.name)}</b><span>${esc(t.d)}</span></button>`).join('')}</div></div>`;
    $$('#spPicker canvas[data-tpl]').forEach(cv=>{ const Wc=cv.clientWidth||160, Hc=cv.clientHeight||104, d=window.devicePixelRatio||1;
      cv.width=Wc*d; cv.height=Hc*d; const g=cv.getContext('2d'); g.scale(d,d); const pal=SP_PAL[sp.paper]; g.fillStyle=pal.paper; g.fillRect(0,0,Wc,Hc);
      spTemplate(g,cv.dataset.tpl,Wc/1000,Hc/(Wc/1000),pal,false); });
    $('#spPkX').onclick=()=>picker(false);
    pk.onclick=e=>{ if(e.target===pk) picker(false); };
    $$('#spPicker [data-tpl]').forEach(b=>{ if(b.tagName!=='BUTTON') return; b.onclick=()=>{ const p=spNewPage(b.dataset.tpl); sp.pages.push(p); sp.cur=p.id; spSave(true); picker(false); pagesPaint(); paintAll(); }; });
  }

  // ── toolbar ──
  const setTool=t=>{ commitTx(); spTool=t; spPrefs(); $$('#appBody [data-tool]').forEach(b=>{ b.classList.toggle('on',b.dataset.tool===t); b.setAttribute('aria-pressed',b.dataset.tool===t); });
    stage.dataset.tool=t; swatchPaint(); gL.clearRect(0,0,W,H); };
  function swatchPaint(){ const pal=SP_PAL[sp.paper]; $$('#spCols [data-col]').forEach(b=>{ b.style.setProperty('--c',spTool==='hl'?pal.hl[b.dataset.col]:pal[b.dataset.col]); b.classList.toggle('on',b.dataset.col===spColor); });
    $('#spCols').classList.toggle('dim',spTool==='er'); }
  $$('#appBody [data-tool]').forEach(b=>b.onclick=()=>setTool(b.dataset.tool));
  $$('#spCols [data-col]').forEach(b=>b.onclick=()=>{ spColor=b.dataset.col; spPrefs(); if(spTool==='er') setTool('pen'); swatchPaint(); });
  $$('#spSizes [data-size]').forEach(b=>b.onclick=()=>{ spSize=+b.dataset.size; spPrefs(); $$('#spSizes [data-size]').forEach(x=>x.classList.toggle('on',x===b)); });
  $('#spUndo').onclick=undo; $('#spRedo').onclick=redo;
  $('#spHints').onclick=()=>{ sp.hints=!sp.hints; spSave(); $('#spHints').classList.toggle('on',sp.hints); paintBg(); };
  $('#spPaper').onclick=()=>{ sp.paper=sp.paper==='night'?'day':'night'; spSave(true); $('#spPaper').textContent=sp.paper==='night'?'NIGHT':'DAY';
    $('.sp').className=`sp sp-${sp.paper}`; swatchPaint(); paintAll(); sp.pages.forEach(spThumb); };
  const clr=$('#spClear');
  clr.onclick=()=>{ const p=spPage();
    if(clr.dataset.armed!=='1'){ clr.dataset.armed='1'; clr.textContent='CONFIRM?'; clr.classList.add('armed'); setTimeout(()=>{ clr.dataset.armed=''; clr.textContent='CLEAR'; clr.classList.remove('armed'); },2500); return; }
    clr.dataset.armed=''; clr.textContent='CLEAR'; clr.classList.remove('armed');
    if(p.tpl==='note'){ p.text=''; note.value=''; spSave(); return; }
    if(!p.items.length) return; push({t:'clr',its:p.items.map((it,ix)=>[it,ix])}); p.items=[]; paintInk(); };
  note.oninput=()=>{ spPage().text=note.value; spSave(); };

  // ── ink input ──
  let act=null, raf=0, liveIx=0, lx=0, ly=0, erasedOp=null;
  const pt=e=>{ const r=live.getBoundingClientRect(), k=uiScaleOf(live); return [(e.clientX-r.left)/k/s,(e.clientY-r.top)/k/s]; };
  function frame(){ raf=0; if(!act) return; const pal=SP_PAL[sp.paper], it=act.it, P=it.p;
    if(it.hl){ gL.clearRect(0,0,W,H); spDrawItem(gL,it,s,pal); return; }
    gL.save(); gL.strokeStyle=spInkColor(it,pal); gL.lineCap='round'; gL.lineJoin='round';
    for(let i=Math.max(1,liveIx);i<P.length;i++){ const a=P[i-1], b=P[i], mx=(a[0]+b[0])/2*s, my=(a[1]+b[1])/2*s;
      gL.beginPath(); gL.moveTo(lx,ly); gL.quadraticCurveTo(a[0]*s,a[1]*s,mx,my); gL.lineWidth=Math.max(.6,(a[2]+b[2])/2*s); gL.stroke(); lx=mx; ly=my; }
    liveIx=P.length; gL.restore(); }
  function eraseAt(x,y){ const p=spPage(), r=SP_ER[spSize]; const hit=[];
    p.items.forEach((it,ix)=>{ if(spHit(it,x,y,r)) hit.push([it,ix]); });
    if(hit.length){ const set=new Set(hit.map(h=>h[0])); p.items=p.items.filter(i=>!set.has(i)); (erasedOp||(erasedOp={t:'del',its:[]})).its.push(...hit); paintInk(); } }
  function eraserCursor(x,y){ gL.clearRect(0,0,W,H); const r=SP_ER[spSize]*s; gL.save(); gL.strokeStyle=SP_PAL[sp.paper].label; gL.lineWidth=1.2; gL.setLineDash([3,3]);
    gL.beginPath(); gL.arc(x*s,y*s,r,0,7); gL.stroke(); gL.restore(); }
  function widthFor(e,prev,now){ const base=SP_SIZES[spSize];
    if(e.pointerType==='pen'&&e.pressure>0&&e.pressure!==.5) return base*(.3+Math.min(1,e.pressure)*1.15);
    if(!prev) return base;
    const dt=Math.max(1,now-prev.t), v=Math.hypot(e.clientX-prev.cx,e.clientY-prev.cy)/dt;   // px per ms
    const target=base*Math.max(.55,Math.min(1.25,1.3-v*.42));
    return prev.w*.62+target*.38; }
  live.addEventListener('pointerdown',e=>{
    if(e.button>0) return;
    if(e.pointerType==='pen'&&!spPenSeen){ spPenSeen=true; $('#spPencil').classList.remove('hidden'); }
    if(spPenSeen&&e.pointerType==='touch') return;                 // palm rejection
    if(act) return;                                                 // one ink pointer at a time
    const [x,y]=pt(e); if(spPage().tpl==='note') return;
    if(spTool==='tx'){ e.preventDefault(); openTx(x,y); return; }
    commitTx(); try{ live.setPointerCapture(e.pointerId); }catch{} e.preventDefault();
    if(spTool==='er'){ act={id:e.pointerId,er:true}; erasedOp=null; eraseAt(x,y); eraserCursor(x,y); return; }
    const hl=spTool==='hl', w=hl?SP_HL[spSize]:widthFor(e,null,e.timeStamp);
    act={id:e.pointerId,it:{k:'s',c:spColor,hl,w,p:[[+x.toFixed(1),+y.toFixed(1),+w.toFixed(2)]]},last:{t:e.timeStamp,cx:e.clientX,cy:e.clientY,w}};
    liveIx=1; lx=x*s; ly=y*s;
    if(!hl){ gL.save(); gL.fillStyle=spInkColor(act.it,SP_PAL[sp.paper]); gL.beginPath(); gL.arc(x*s,y*s,w*s/2,0,7); gL.fill(); gL.restore(); }
    else if(!raf) raf=requestAnimationFrame(frame);
  });
  live.addEventListener('pointermove',e=>{
    if(!act){ if(spTool==='er'&&e.pointerType!=='touch'){ const [x,y]=pt(e); eraserCursor(x,y); } return; }
    if(e.pointerId!==act.id) return; e.preventDefault();
    const evs=e.getCoalescedEvents?e.getCoalescedEvents():[e];
    for(const ev of (evs.length?evs:[e])){ const [x,y]=pt(ev);
      if(act.er){ eraseAt(x,y); eraserCursor(x,y); continue; }
      const P=act.it.p, L=P[P.length-1]; if(Math.hypot(x-L[0],y-L[1])<.9) continue;
      const w=act.it.hl?act.it.w:widthFor(ev,act.last,ev.timeStamp); act.last={t:ev.timeStamp,cx:ev.clientX,cy:ev.clientY,w};
      P.push([+x.toFixed(1),+y.toFixed(1),+w.toFixed(2)]); }
    if(!act.er&&!raf) raf=requestAnimationFrame(frame);
  });
  const end=e=>{ if(!act||e.pointerId!==act.id) return;
    if(act.er){ if(erasedOp){ push(erasedOp); erasedOp=null; } act=null; if(e.pointerType==='touch') gL.clearRect(0,0,W,H); return; }
    const it=act.it; act=null; if(raf){ cancelAnimationFrame(raf); raf=0; }
    gL.clearRect(0,0,W,H); spPage().items.push(it); spDrawItem(gI,it,s,SP_PAL[sp.paper]); push({t:'add',it}); };
  live.addEventListener('pointerup',end); live.addEventListener('pointercancel',end);
  live.addEventListener('pointerleave',()=>{ if(!act&&spTool==='er') gL.clearRect(0,0,W,H); });

  // ── text stamps ──
  let tx=null;
  function openTx(x,y){ commitTx(); const K=spKnown(), now=new Date().toISOString().substr(11,5).replace(':','')+'Z';
    tx={x,y}; const box=$('#spTx'), inp=$('#spTxIn');
    const chips=[['UTC',now],['SQK',K.squawk],['QNH',K.arrQnh||K.depQnh],['RWY',K.depRwy],['SID',K.sid],['CALLSIGN',K.callsign],['FL',K.cruise]].filter(c=>c[1]);
    $('#spChips').innerHTML=chips.map(([l,v])=>`<button data-v="${esc(v)}"><small>${l}</small>${esc(v)}</button>`).join('');
    box.style.left=Math.min(W-280,Math.max(4,x*s-4))+'px'; box.style.top=Math.max(4,y*s-SP_TXT[spSize]*s-12)+'px';
    box.classList.remove('hidden'); inp.value=''; inp.style.fontSize=Math.max(14,SP_TXT[spSize]*s)+'px'; setTimeout(()=>inp.focus(),0);
    $$('#spChips [data-v]').forEach(b=>b.onpointerdown=ev=>{ ev.preventDefault(); inp.value=(inp.value?inp.value+' ':'')+b.dataset.v; inp.focus(); });
  }
  function commitTx(){ if(!tx) return; const v=$('#spTxIn').value.trim(), at=tx; tx=null; $('#spTx').classList.add('hidden');
    if(!v) return; const it={k:'t',c:spColor,x:+at.x.toFixed(1),y:+at.y.toFixed(1),z:SP_TXT[spSize],v};
    spPage().items.push(it); spDrawItem(gI,it,s,SP_PAL[sp.paper]); push({t:'add',it}); }
  $('#spTxIn').onkeydown=e=>{ if(e.key==='Enter'){ e.preventDefault(); commitTx(); } else if(e.key==='Escape'){ e.preventDefault(); tx=null; $('#spTx').classList.add('hidden'); } e.stopPropagation(); };
  $('#spTxIn').onblur=()=>setTimeout(()=>{ if(tx&&document.activeElement!==$('#spTxIn')) commitTx(); },120);

  // ── keyboard ──
  const keys=e=>{ if(activeApp!=='scratch'||/INPUT|TEXTAREA/.test(document.activeElement?.tagName||'')) return;
    const k=e.key.toLowerCase();
    if((e.ctrlKey||e.metaKey)&&k==='z'){ e.preventDefault(); e.shiftKey?redo():undo(); return; }
    if((e.ctrlKey||e.metaKey)&&k==='y'){ e.preventDefault(); redo(); return; }
    if(e.ctrlKey||e.metaKey||e.altKey) return;
    const t={p:'pen',h:'hl',e:'er',t:'tx'}[k]; if(t){ e.preventDefault(); setTool(t); } };
  document.addEventListener('keydown',keys);

  const ro=new ResizeObserver(()=>{ if(Math.abs(stage.clientWidth-W)>1||Math.abs(stage.clientHeight-H)>1) layout(); });
  ro.observe(stage);
  appCleanup=()=>{ commitTx(); ro.disconnect(); document.removeEventListener('keydown',keys); if(raf) cancelAnimationFrame(raf); spSave(true); };
  stage.dataset.tool=spTool; pagesPaint(); swatchPaint(); layout();
}


// ── MINI GAME: "Approach!" ──
// A calm, one-thumb game for an EFB held at an angle: fly the "highway in the sky" gates,
// then land. Hold anywhere (or Space / ↑) to climb, let go to descend; vertical speed eases
// smoothly, so there's nothing twitchy. DRAG mode moves the aircraft with a thumb instead.
//   Gates   — wide synthetic-vision boxes; every gate is reachable from the last. The centre
//             band scores PERFECT. Missing a gate is a go-around (a life), never instant death.
//   Landing — every 8 gates the runway comes into sight: follow the PAPI down, flare by holding,
//             and the touchdown rate is graded BUTTER / SMOOTH / FIRM / HARD, like the tracker.
//   Legs    — each landing starts a new leg between AviatesAir airports, a little faster.
// Frame-rate independent (dt), paused when hidden or when you go home (no background CPU).
const GM_KEY='efbGameV2', GM_PREF='efbGamePrefs';
const GM_ROUTE=['EGNX','EGLL','LSZH','LEMD','LFPG','EHAM','EDDF','LIRF','LPPT','EIDW','EKCH','LOWW','EGPH','LGAV','EGNX'];
function renderGame(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  let prefs={ctrl:'hold',mode:'relaxed'}; try{ Object.assign(prefs,JSON.parse(localStorage.getItem(GM_PREF)||'{}')); }catch{}
  let best={relaxed:0,classic:0,butter:0}; try{ Object.assign(best,JSON.parse(localStorage.getItem(GM_KEY)||'{}')); }catch{}
  const seg=(k,opts)=>`<div class="gm-seg" data-k="${k}">${opts.map(([v,l,s])=>`<button data-v="${v}" class="${prefs[k]===v?'on':''}"><b>${l}</b><small>${s}</small></button>`).join('')}</div>`;
  body.innerHTML=`<div class="gm">
    <canvas id="gmCv"></canvas>
    <div class="gm-hud hidden" id="gmHud">
      <div class="gm-score"><b id="gmScore">0</b><span id="gmLives"></span></div>
      <div class="gm-leg mono" id="gmLeg"></div>
      <div class="gm-r"><span class="mono" id="gmBest"></span><button class="gm-pause" id="gmPause" aria-label="Pause"><i></i><i></i></button></div>
    </div>
    <div class="gm-hint hidden" id="gmHint"></div>
    <div class="gm-toast" id="gmToast"></div>
    <div class="gm-ov" id="gmOv"><div class="gm-card" id="gmCard"></div></div>
  </div>`;
  const cv=$('#gmCv'), stage=cv.parentElement, g=cv.getContext('2d');
  let W=0,H=0,dpr=1;
  function size(){ W=stage.clientWidth; H=stage.clientHeight; dpr=Math.min(2,window.devicePixelRatio||1);
    cv.width=W*dpr; cv.height=H*dpr; cv.style.width=W+'px'; cv.style.height=H+'px'; g.setTransform(dpr,0,0,dpr,0,0); }
  size();

  // ── scenery (procedural, seeded so it tiles smoothly) ──
  const ridge=(seed,amp,freq)=>x=>{ let v=0; for(let i=1;i<=4;i++) v+=Math.sin(x*freq*i*1.37+seed*i*2.1)/(i*1.25); return v*amp; };
  const farR=ridge(1.3,1,0.004), nearR=ridge(4.1,1,0.007);
  const stars=[...Array(70)].map(()=>({x:Math.random(),y:Math.random()*.45,r:Math.random()*1.2+.3,p:Math.random()*6}));
  const clouds=[...Array(7)].map((_,i)=>({x:Math.random()*1.4,y:.12+Math.random()*.42,w:.14+Math.random()*.16,s:.25+Math.random()*.35}));
  const lights=[...Array(60)].map(()=>({x:Math.random()*3,o:Math.random()}));

  // ── game state ──
  let S=null, raf=0, last=0, paused=false, holding=false, drag=null, started=false;
  const cfg=()=>prefs.mode==='relaxed'?{lives:5,gap:.24,shrink:.006,minGap:.17,speed:.27,accel:.03,reach:.45,rwy:1.15,grace:.3}:{lives:3,gap:.2,shrink:.008,minGap:.14,speed:.34,accel:.05,reach:.55,rwy:.85,grace:.1};
  function newGame(){ const c=cfg();
    S={c, x:0, y:H*.45, vy:0, target:null, score:0, lives:c.lives, gates:[], passed:0, leg:0, legGates:0, phase:'fly', rwy:null,
       speed:W*c.speed, particles:[], popups:[], shake:0, landings:0, bestTd:null, t:0, invuln:0, spawnX:W*.95};
    for(let i=0;i<4;i++) spawnGate();
    legLabel(); hud(); }
  const groundY=()=>H*.86, planeX=()=>Math.max(70,W*.26);
  function spawnGate(){ const c=S.c, gap=Math.max(c.minGap,c.gap-c.shrink*S.leg)*H, prev=S.gates[S.gates.length-1];
    const spacing=Math.max(W*.55,340), tBetween=spacing/S.speed, reach=H*.3*tBetween*c.reach;
    const lo=H*.16+gap/2, hi=groundY()-H*.12-gap/2;
    const base=prev?prev.cy:H*.45, cy=Math.max(lo,Math.min(hi,base+(Math.random()*2-1)*reach));
    S.gates.push({x:S.spawnX, cy, h:gap/2, state:0, fx:0}); S.spawnX+=spacing; }
  function legLabel(){ const a=GM_ROUTE[S.leg%(GM_ROUTE.length-1)], b=GM_ROUTE[S.leg%(GM_ROUTE.length-1)+1];
    S.from=a; S.to=b; $('#gmLeg').innerHTML=`LEG ${S.leg+1} · ${a} <em>→</em> ${b}`; }
  function hud(){ $('#gmScore').textContent=S.score;
    $('#gmLives').innerHTML=[...Array(S.c.lives)].map((_,i)=>`<i class="${i<S.lives?'':'off'}"></i>`).join('');
    $('#gmBest').textContent=`BEST ${Math.max(best[prefs.mode]||0,S.score)}`; }
  function toast(t,cls=''){ const el=$('#gmToast'); el.className='gm-toast '+cls; el.textContent=t; void el.offsetWidth; el.classList.add('show'); }
  function pop(txt,x,y,col){ S.popups.push({txt,x,y,col,t:0}); }
  function loseLife(why){ if(S.invuln>0) return; S.lives--; S.invuln=1.6; S.shake=.35; toast(why,'bad'); hud();
    if(navigator.vibrate) try{ navigator.vibrate(40); }catch{}
    if(S.lives<=0) setTimeout(gameOver,650); }

  // ── input: hold anywhere / Space / ↑, or relative drag ──
  const down=e=>{ if(!started||paused||!S) return; if(e.target.closest('.gm-hud,.gm-ov')) return; e.preventDefault();
    if(prefs.ctrl==='drag'){ drag={y0:e.clientY,t0:S.y}; S.target=S.y; } else holding=true; };
  const move=e=>{ if(drag&&S){ S.target=Math.max(H*.08,Math.min(groundY()-8,drag.t0+(e.clientY-drag.y0)/uiScaleOf(stage)*1.5)); } };
  const up=()=>{ holding=false; drag=null; if(S) S.target=null; };
  stage.addEventListener('pointerdown',down); window.addEventListener('pointermove',move); window.addEventListener('pointerup',up); window.addEventListener('pointercancel',up);
  const kd=e=>{ if(activeApp!=='game') return; if(e.code==='Space'||e.code==='ArrowUp'){ e.preventDefault(); if(!started) return start(); holding=true; }
    if(e.code==='KeyP'&&started){ e.preventDefault(); togglePause(); } };
  const ku=e=>{ if(e.code==='Space'||e.code==='ArrowUp') holding=false; };
  window.addEventListener('keydown',kd); window.addEventListener('keyup',ku);
  const vis=()=>{ if(document.hidden&&started&&!paused) togglePause(); };
  document.addEventListener('visibilitychange',vis);

  // ── physics & rules ──
  function step(dt){
    const c=S.c, gY=groundY(), px=planeX(); S.t+=dt; S.invuln=Math.max(0,S.invuln-dt); S.shake=Math.max(0,S.shake-dt);
    // vertical: ease towards a target vertical speed (hold) or target height (drag)
    // ground effect / flare assist on short final: below ~70 px the sink eases off and a hold
    // arrests it gently instead of zooming back up, so the flare is forgiving but still rewarded
    const agl=gY-6-S.y, ge=S.phase==='land'?Math.max(.3,Math.min(1,agl/70)):1;   // hands-off settles at ~250 fpm (SMOOTH)
    const climb=-H*.33*(S.phase==='land'?Math.max(.18,Math.min(1,agl/90)):1), sink=H*(S.phase==='land'?.2:.27)*ge;
    let tv;
    if(prefs.ctrl==='drag'&&S.target!=null) tv=Math.max(climb,Math.min(sink*1.3,(S.target-S.y)*4));
    else tv=holding?climb:sink;
    if(S.phase==='roll'||S.phase==='takeoff') tv=S.phase==='takeoff'?climb*.8:0;
    S.vy+=(tv-S.vy)*Math.min(1,dt*(holding||drag?4.2:ge<1?5.5:2.6));   // ground effect settles quickly
    S.y+=S.vy*dt;
    if(S.y<H*.07){ S.y=H*.07; S.vy=Math.max(0,S.vy); }
    // world scroll
    const spd=S.phase==='roll'?S.speed*Math.max(.15,1-S.rollT*1.1):S.speed;
    S.x+=spd*dt; const dx=spd*dt;
    S.gates.forEach(o=>o.x-=dx); if(S.rwy){ S.rwy.x-=dx; } S.spawnX-=dx;
    // gates
    for(const o of S.gates){
      if(o.state===0&&o.x<=px){ const off=Math.abs(S.y-o.cy);
        if(off<=o.h*(1+c.grace)){ const perfect=off<=o.h*.35, close=off>o.h; o.state=perfect?2:1; S.score+=perfect?2:1; S.passed++; S.legGates++;
          pop(perfect?'PERFECT +2':close?'CLOSE +1':'+1',px+30,o.cy-o.h-8,perfect?'#48ec82':close?'#ffac30':'#e6e7ea'); hud(); }
        else { o.state=3; S.legGates++; loseLife('GO AROUND · MISSED GATE'); } }
      if(o.state) o.fx=Math.min(1,o.fx+dt*2.2);
    }
    S.gates=S.gates.filter(o=>o.x>-80);
    if(S.phase==='fly'){
      if(S.legGates+S.gates.filter(o=>!o.state).length<8){ while(S.gates.length<5&&S.legGates+S.gates.filter(o=>!o.state).length<8) spawnGate(); }
      else if(!S.gates.some(o=>!o.state)&&!S.rwy){                         // leg's gates done → runway ahead
        S.phase='land'; const len=Math.max(W*S.c.rwy,420); S.rwy={x:W*1.25,len,done:false}; toast(`RUNWAY IN SIGHT · ${S.to}`); }
    }
    // ground contact
    const onRwy=S.rwy&&px>=S.rwy.x&&px<=S.rwy.x+S.rwy.len;
    if(S.y>=gY-6){
      if(S.phase==='land'&&onRwy){ touchdown(); }
      else if(S.phase==='roll'||S.phase==='takeoff'){ S.y=gY-6; }
      else { S.y=gY-6; S.vy=-H*.25; loseLife(S.phase==='land'?'SHORT OF THE RUNWAY':'TERRAIN · PULL UP'); }
    }
    if(S.phase==='land'&&S.rwy&&px>S.rwy.x+S.rwy.len&&!S.rwy.done){ S.rwy.done=true; toast('GO AROUND · RUNWAY OVERSHOT','bad'); nextLeg(false); }
    if(S.phase==='roll'){ S.rollT+=dt; S.y=gY-6; S.vy=0; if(S.rollT>1.4){ S.phase='takeoff'; S.toT=0; toast(`CLEARED FOR TAKEOFF · ${S.from} → ${S.to}`); } }
    if(S.phase==='takeoff'){ S.toT+=dt; if(S.toT>1.1){ S.phase='fly'; S.rwy=null; S.spawnX=Math.max(S.spawnX,W*1.05); for(let i=0;i<4;i++) spawnGate(); } }
    // contrail & popups
    if(S.phase!=='roll'&&Math.random()<dt*40) S.particles.push({x:px-26,y:S.y+2,t:0,r:2+Math.random()*2});
    S.particles.forEach(p=>{ p.x-=dx*.9; p.t+=dt; p.r+=dt*6; }); S.particles=S.particles.filter(p=>p.t<1.6);
    S.popups.forEach(p=>{ p.t+=dt; p.y-=dt*28; }); S.popups=S.popups.filter(p=>p.t<1.1);
  }
  function touchdown(){
    const fpm=Math.round(S.vy/H*4200);                     // screen sink → a believable fpm
    const [g1,pts,col]=fpm<=150?['BUTTER',10,'#48ec82']:fpm<=260?['SMOOTH',6,'#a3d34a']:fpm<=450?['FIRM',3,'#ffac30']:['HARD',1,'#ff6b6b'];
    S.score+=pts; S.landings++; S.bestTd=S.bestTd==null?fpm:Math.min(S.bestTd,fpm);
    pop(`${g1} ${fpm} FPM +${pts}`,planeX()+20,groundY()-60,col); toast(`${g1} LANDING · ${fpm} FPM`,fpm<=260?'good':'');
    S.phase='roll'; S.rollT=0; S.y=groundY()-6; S.vy=0; S.rwy.done=true; hud(); nextLeg(true);
  }
  function nextLeg(landed){ S.leg++; S.legGates=0; S.speed*=1+S.c.accel; legLabel();
    if(!landed){ S.phase='fly'; S.rwy=null; S.spawnX=Math.max(S.spawnX,W*1.05); for(let i=0;i<4;i++) spawnGate(); } }

  // ── drawing ──
  const acc='#4D9BEC';
  function draw(){
    const gY=groundY(), px=planeX(), sx=S?S.x:performance.now()*.05, sh=S&&S.shake?Math.sin(S.t*80)*S.shake*8:0;
    g.save(); g.translate(0,sh);
    // sky: deep navy to a warm horizon
    const sky=g.createLinearGradient(0,0,0,gY);
    sky.addColorStop(0,'#070a14'); sky.addColorStop(.45,'#1b2442'); sky.addColorStop(.78,'#6b3b5e'); sky.addColorStop(1,'#f08a5d');
    g.fillStyle=sky; g.fillRect(0,-10,W,H+20);
    const now=performance.now()/1000;
    stars.forEach(s=>{ g.globalAlpha=.35+.35*Math.sin(now*1.6+s.p); g.fillStyle='#fff'; g.beginPath(); g.arc(s.x*W,s.y*H,s.r,0,7); g.fill(); }); g.globalAlpha=1;
    // sun glow on the horizon
    const sunX=W*.72, sunY=gY-H*.1, sg=g.createRadialGradient(sunX,sunY,0,sunX,sunY,H*.55);
    sg.addColorStop(0,'rgba(255,214,150,.95)'); sg.addColorStop(.08,'rgba(255,180,110,.75)'); sg.addColorStop(.3,'rgba(240,110,90,.25)'); sg.addColorStop(1,'rgba(240,110,90,0)');
    g.fillStyle=sg; g.fillRect(0,0,W,gY);
    // clouds (mid parallax)
    clouds.forEach(c=>{ const x=((c.x*W-sx*c.s*.25)%(W*1.4)+W*1.4)%(W*1.4)-W*.2, y=c.y*H, w=c.w*W;
      const cg=g.createRadialGradient(x,y,0,x,y,w); cg.addColorStop(0,'rgba(255,190,170,.18)'); cg.addColorStop(1,'rgba(255,190,170,0)');
      g.fillStyle=cg; g.beginPath(); g.ellipse(x,y,w,w*.28,0,0,7); g.fill(); });
    // mountains: far and near ridges
    const ridgeLayer=(fn,par,base,amp,col)=>{ g.fillStyle=col; g.beginPath(); g.moveTo(0,gY);
      for(let x=0;x<=W+8;x+=8) g.lineTo(x,base-fn(x+sx*par)*amp-amp); g.lineTo(W,gY); g.closePath(); g.fill(); };
    ridgeLayer(farR,.12,gY-H*.04,H*.07,'#3a2c4d'); ridgeLayer(nearR,.3,gY+H*.01,H*.06,'#1f1a2e');
    // ground with town lights
    const gg=g.createLinearGradient(0,gY,0,H); gg.addColorStop(0,'#141320'); gg.addColorStop(1,'#07070c'); g.fillStyle=gg; g.fillRect(0,gY,W,H-gY+10);
    lights.forEach(l=>{ const x=((l.x*W-sx*.9)%(W*3)+W*3)%(W*3)-W*.1; if(x<-4||x>W+4) return;
      g.globalAlpha=.4+.4*Math.sin(now*2+l.o*9); g.fillStyle=l.o>.8?'#8fd3ff':'#ffc36b'; g.fillRect(x,gY+6+l.o*(H-gY-14),2,2); }); g.globalAlpha=1;
    if(S){ drawRunway(gY); drawGates(); drawTrail(); }
    drawPlane(S?px:W*.3, S?S.y:H*.45+Math.sin(now*1.3)*H*.03, S?Math.atan2(S.vy,S.speed)*.9:Math.sin(now*1.3)*.05);
    if(S){ drawPopups(); if(S.phase==='land'&&S.rwy) drawPapi(); }
    g.restore();
  }
  function drawGates(){
    S.gates.forEach(o=>{ const x=o.x, top=o.cy-o.h, h=o.h*2, w=Math.max(26,W*.035);
      const col=o.state===3?'#ff6b6b':o.state===2?'#48ec82':o.state===1?'#e6e7ea':'#ff5ad1';
      g.save(); g.globalAlpha=o.state?1-o.fx*.85:1;
      g.shadowColor=col; g.shadowBlur=14; g.strokeStyle=col; g.lineWidth=3;
      g.beginPath(); g.roundRect(x-w/2,top,w,h,8); g.stroke(); g.shadowBlur=0;
      g.fillStyle=col; g.globalAlpha*=.08; g.fillRect(x-w/2,top,w,h); g.globalAlpha=o.state?1-o.fx*.85:1;
      // centre-band chevrons
      g.strokeStyle=col; g.lineWidth=2; const cb=o.h*.35;
      [top+o.h-cb,top+o.h+cb].forEach(yy=>{ g.beginPath(); g.moveTo(x-w/2-10,yy); g.lineTo(x-w/2+4,yy); g.moveTo(x+w/2-4,yy); g.lineTo(x+w/2+10,yy); g.stroke(); });
      g.restore(); });
  }
  function drawRunway(gY){ const r=S.rwy; if(!r) return;
    const x=r.x, len=r.len;
    // approach path guide (dashed) back from the aiming point
    if(S.phase==='land'){ g.save(); g.setLineDash([8,10]); g.strokeStyle='rgba(255,90,209,.55)'; g.lineWidth=2;
      g.beginPath(); g.moveTo(x+len*.12,gY-6); g.lineTo(x-W*.9,gY-6-W*.9*.28); g.stroke(); g.restore(); }
    g.fillStyle='#2a2b33'; g.fillRect(x,gY-3,len,7);
    g.fillStyle='#e8e8e8'; for(let i=0;i<len;i+=34) g.fillRect(x+i+6,gY-1,16,2);
    g.fillStyle='#48ec82'; g.fillRect(x-3,gY-4,4,9); g.fillStyle='#ff6b6b'; g.fillRect(x+len-1,gY-4,4,9);
    for(let i=0;i<len;i+=26){ g.globalAlpha=.8; g.fillStyle='#fff4d0'; g.beginPath(); g.arc(x+i,gY-4,1.6,0,7); g.fill(); } g.globalAlpha=1;
  }
  function drawPapi(){ const r=S.rwy, px=planeX(), gY=groundY();
    const ideal=gY-6-Math.max(0,(r.x+r.len*.12-px))*.28, dev=(ideal-S.y)/H;   // + = above the path
    const whites=dev>.06?4:dev>.02?3:dev>-.02?2:dev>-.06?1:0;
    const bx=W-150, by=74;
    g.save(); g.fillStyle='rgba(10,12,18,.72)'; g.beginPath(); g.roundRect(bx,by,132,48,8); g.fill();
    g.fillStyle='rgba(255,255,255,.55)'; g.font='700 9px ui-monospace,Consolas,monospace'; g.fillText('PAPI',bx+10,by+15);
    for(let i=0;i<4;i++){ const on=i<whites; g.fillStyle=on?'#ffffff':'#ff3b3b'; g.shadowColor=g.fillStyle; g.shadowBlur=10;
      g.beginPath(); g.arc(bx+22+i*24,by+31,6.5,0,7); g.fill(); }
    g.shadowBlur=0; const fpm=Math.round(S.vy/H*4200);
    g.fillStyle='rgba(255,255,255,.8)'; g.font='700 10px ui-monospace,Consolas,monospace'; g.fillText(`V/S ${fpm>0?'-':'+'}${Math.abs(fpm)}`,bx+56,by+15);
    g.restore(); }
  function drawTrail(){ S.particles.forEach(p=>{ g.globalAlpha=Math.max(0,.28*(1-p.t/1.6)); g.fillStyle='#ffffff'; g.beginPath(); g.arc(p.x,p.y,p.r,0,7); g.fill(); }); g.globalAlpha=1; }
  function drawPopups(){ S.popups.forEach(p=>{ g.globalAlpha=Math.max(0,1-p.t/1.1); g.fillStyle=p.col; g.font='800 14px -apple-system,"Segoe UI",sans-serif'; g.fillText(p.txt,p.x,p.y); }); g.globalAlpha=1; }
  function drawPlane(x,y,ang){
    const k=Math.max(.8,Math.min(1.35,H/620)), blink=(performance.now()%1200)<90;
    g.save(); g.translate(x,y); g.rotate(ang); g.scale(k,k);
    if(S&&S.invuln>0&&Math.floor(S.invuln*10)%2) g.globalAlpha=.45;
    g.shadowColor='rgba(0,0,0,.45)'; g.shadowBlur=12; g.shadowOffsetY=4;
    // fuselage
    const fg=g.createLinearGradient(0,-9,0,9); fg.addColorStop(0,'#ffffff'); fg.addColorStop(1,'#c9ced8');
    g.fillStyle=fg; g.beginPath(); g.moveTo(-34,-4); g.quadraticCurveTo(-38,0,-34,5); g.lineTo(22,6); g.quadraticCurveTo(38,5,40,0); g.quadraticCurveTo(34,-6,20,-6); g.closePath(); g.fill();
    g.shadowBlur=0; g.shadowOffsetY=0;
    // tail fin (brand)
    g.fillStyle=acc; g.beginPath(); g.moveTo(-30,-4); g.lineTo(-40,-22); g.lineTo(-33,-22); g.lineTo(-20,-5); g.closePath(); g.fill();
    g.fillStyle='#2f6fb8'; g.beginPath(); g.moveTo(-34,1); g.lineTo(-44,4); g.lineTo(-30,4); g.closePath(); g.fill();
    // wing + engine
    g.fillStyle='#aeb5c2'; g.beginPath(); g.moveTo(4,2); g.lineTo(-10,11); g.lineTo(-4,11); g.lineTo(14,3); g.closePath(); g.fill();
    g.fillStyle='#8c93a1'; g.beginPath(); g.roundRect(-2,5,15,6,3); g.fill();
    // cheatline, windows, cockpit
    g.fillStyle=acc; g.fillRect(-30,1.2,52,1.4);
    g.fillStyle='#39465d'; for(let i=-22;i<18;i+=5) g.fillRect(i,-2.4,2.4,2);
    g.beginPath(); g.moveTo(30,-3.5); g.lineTo(36,-1.5); g.lineTo(29,-1); g.closePath(); g.fill();
    // strobe
    if(blink){ g.fillStyle='#fff'; g.shadowColor='#fff'; g.shadowBlur=16; g.beginPath(); g.arc(-40,-21,2.2,0,7); g.fill(); }
    g.restore(); }

  // ── loop / flow ──
  function frame(ts){ raf=requestAnimationFrame(frame); const dt=Math.min(.05,(ts-(last||ts))/1000); last=ts;
    if(started&&!paused&&S&&S.lives>0) step(dt);
    draw(); }
  function start(){ const c=$('#gmOv'); c.classList.add('out'); setTimeout(()=>c.classList.add('hidden'),260);
    size(); newGame(); started=true; paused=false; $('#gmHud').classList.remove('hidden');
    const hint=$('#gmHint'); hint.textContent=prefs.ctrl==='drag'?'DRAG ANYWHERE TO FLY':'HOLD ANYWHERE TO CLIMB, LET GO TO DESCEND';
    hint.classList.remove('hidden'); setTimeout(()=>hint.classList.add('hidden'),3800); }
  function togglePause(){ if(!started||!S||S.lives<=0) return; paused=!paused; holding=false; drag=null;
    if(paused) card('pause'); else { const o=$('#gmOv'); o.classList.add('out'); setTimeout(()=>o.classList.add('hidden'),220); } }
  function gameOver(){ started=false; const m=prefs.mode, nb=S.score>(best[m]||0);
    if(nb) best[m]=S.score; if(S.bestTd!=null&&(!best.butter||S.bestTd<best.butter)) best.butter=S.bestTd;
    try{ localStorage.setItem(GM_KEY,JSON.stringify(best)); }catch{}
    card('over',nb); $('#gmHud').classList.add('hidden'); }
  function card(kind,newBest){
    const ov=$('#gmOv'), c=$('#gmCard'); ov.classList.remove('hidden','out');
    const stats=S?`<div class="gm-stats"><div><b>${S.score}</b><small>SCORE</small></div><div><b>${S.passed}</b><small>GATES</small></div><div><b>${S.landings}</b><small>LANDINGS</small></div><div><b>${S.bestTd!=null?S.bestTd:'—'}</b><small>BEST FPM</small></div></div>`:'';
    if(kind==='pause'){ c.innerHTML=`<div class="gm-t">Paused</div><p class="gm-s">Leg ${S.leg+1}, ${S.from} to ${S.to}</p>${stats}
      <div class="gm-btns"><button class="gm-go" id="gmResume">RESUME</button><button class="gm-ghost" id="gmQuit">END FLIGHT</button></div>`;
      $('#gmResume').onclick=togglePause; $('#gmQuit').onclick=()=>{ paused=false; S.lives=0; gameOver(); }; return; }
    const over=kind==='over';
    c.innerHTML=`<div class="gm-t">${over?(newBest?'New best':'Flight complete'):'Approach!'}</div>
      <p class="gm-s">${over?`${S.landings} landing${S.landings===1?'':'s'} across ${S.leg} leg${S.leg===1?'':'s'}.`:'Fly the gates, follow the PAPI, and grease the landing.'}</p>
      ${over?stats:`<ol class="gm-how"><li>Hold anywhere to climb, let go to descend.</li>
        <li>Fly through the magenta gates. The centre band scores double.</li>
        <li>On the runway, hold to flare for a soft touchdown.</li></ol>`}
      ${seg('ctrl',[['hold','HOLD','anywhere'],['drag','DRAG','one thumb']])}
      ${seg('mode',[['relaxed','RELAXED','5 lives, wide gates'],['classic','CLASSIC','3 lives']])}
      <button class="gm-go" id="gmStart">${over?'FLY AGAIN':'START'}</button>
      <div class="gm-bests mono">BEST ${best.relaxed||0} RELAXED · ${best.classic||0} CLASSIC${best.butter?` · BUTTER ${best.butter} FPM`:''}</div>`;
    $$('#gmCard .gm-seg').forEach(sg=>sg.querySelectorAll('button').forEach(b=>b.onclick=()=>{ prefs[sg.dataset.k]=b.dataset.v;
      try{ localStorage.setItem(GM_PREF,JSON.stringify(prefs)); }catch{} sg.querySelectorAll('button').forEach(x=>x.classList.toggle('on',x===b)); }));
    $('#gmStart').onclick=start;
  }
  $('#gmPause').onclick=togglePause;
  card('start');
  const ro=new ResizeObserver(()=>{ const w=stage.clientWidth,h=stage.clientHeight; if(Math.abs(w-W)>2||Math.abs(h-H)>2){ size(); if(S&&started) { S.y=Math.min(S.y,groundY()-8); } } }); ro.observe(stage);
  raf=requestAnimationFrame(frame);
  appCleanup=()=>{ cancelAnimationFrame(raf); ro.disconnect(); stage.removeEventListener('pointerdown',down);
    window.removeEventListener('pointermove',move); window.removeEventListener('pointerup',up); window.removeEventListener('pointercancel',up);
    window.removeEventListener('keydown',kd); window.removeEventListener('keyup',ku); document.removeEventListener('visibilitychange',vis); };
}

// ── SETTINGS APP ──
// iPadOS Settings layout on the cockpit charcoal: a grouped list on the left, the page on the
// right (one at a time on a phone). Units and theme live on the host, so every paired device
// follows; dark/light is per device.
const THEMES=[{id:'aviates',name:'Aviates',c1:'#4D9BEC',c2:'#0A0D17'},{id:'boeing',name:'Boeing',c1:'#2ec5c9',c2:'#05080d'},
  {id:'airbus',name:'Airbus',c1:'#1e88e5',c2:'#0a0f16'},{id:'minimal',name:'Minimal',c1:'#c9ced8',c2:'#0c0d10'}];
let hello={};
let st={sec:null, pane:'list', rmArm:false, endArm:false, devs:null};
const ST_SECS=[
  {g:'General', items:[
    {id:'display', name:'Display', c:'#4D9BEC', ico:`<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4"/>`},
    {id:'wallpaper', name:'Wallpaper', c:'#2ec5c9', ico:`<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="m3 16 5-5 4 4 3-3 6 6"/><circle cx="16" cy="9" r="1.6"/>`},
    {id:'units', name:'Units', c:'#34c759', ico:`<path d="M6 20h12M12 4v16M5 8h14M5 8l-2.5 6a2.5 2.5 0 0 0 5 0L5 8ZM19 8l-2.5 6a2.5 2.5 0 0 0 5 0L19 8Z"/>`},
  ]},
  {g:'Flight', items:[
    {id:'plan', name:'Flight plan', c:'#ff9f0a', ico:`<path d="M6 3h9l4 4v14H6z"/><path d="M15 3v4h4M9 11h7M9 15h7"/>`},
    {id:'fmode', name:'Flight mode', c:'#30b0c7', ico:`<path d="M3 20h18"/><path d="M5 16 19 7"/><path d="M15 6.5 19 7l-1.2 4"/>`},
    {id:'flight', name:'Active flight', c:'#ff453a', ico:`<path d="M21 15.5v-2L13.5 9V4.2a1.5 1.5 0 0 0-3 0V9L3 13.5v2l7.5-2.3V18L8 19.8V21l4-1 4 1v-1.2L13.5 18v-4.8L21 15.5Z"/>`},
  ]},
  {g:'Connections', items:[
    {id:'datalink', name:'Datalink', c:'#bf5af2', ico:`<path d="M5 12a7 7 0 0 1 14 0M8.5 12a3.5 3.5 0 0 1 7 0"/><circle cx="12" cy="12" r="1.2"/><path d="M12 13v8"/>`},
    {id:'devices', name:'Devices', c:'#8e8e93', ico:`<rect x="4" y="3" width="11" height="18" rx="2"/><rect x="13" y="9" width="8" height="12" rx="1.6"/><path d="M8.5 18h2"/>`},
    {id:'about', name:'About', c:'#636366', ico:`<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.5v.5"/>`},
  ]},
];
const ST_ALL=ST_SECS.flatMap(g=>g.items);

function renderSettings(body){
  body=body||$('#appBody'); body.classList.add('nopad');
  if(!st.sec) st.sec=matchMedia('(min-width:761px)').matches?'display':null;
  st.pane=st.sec&&!matchMedia('(min-width:761px)').matches?'page':'list';
  st.rmArm=false; st.endArm=false;
  const me=msg.myPilotName||hello.pilotName||'Pilot';
  body.innerHTML=`<div class="st" id="st" data-pane="${st.pane}">
    <aside class="st-side">
      <div class="st-ttl">Settings</div>
      <button class="st-me" data-sec="about"><span class="st-av">${esc(initials(me))}</span>
        <span><b>${esc(me)}</b><small>${esc(hello.airline||'AviatesAir')} EFB${hello.version?' '+esc(hello.version.split('.').slice(0,3).join('.')):''}</small></span></button>
      ${ST_SECS.map(g=>`<div class="st-grp"><div class="st-gl">${g.g}</div>${g.items.map(it=>`<button class="st-row" data-sec="${it.id}">
        <span class="st-ic" style="--c:${it.c}"><svg viewBox="0 0 24 24">${it.ico}</svg></span><span class="st-rn">${it.name}</span>
        <span class="st-rv" id="stv-${it.id}"></span><svg class="st-chev" viewBox="0 0 24 24"><path d="m9 6 6 6-6 6"/></svg></button>`).join('')}</div>`).join('')}
    </aside>
    <section class="st-page" id="stPage"></section>
  </div>`;
  $$('#st [data-sec]').forEach(b=>b.onclick=()=>stOpen(b.dataset.sec));
  stValues(); stPage();
  const iv=setInterval(()=>{ if(st.sec==='about'||st.sec==='devices'||st.sec==='datalink') stPage(true); },5000);
  appCleanup=()=>clearInterval(iv);
}
function stOpen(id){ st.sec=id; st.pane='page'; st.rmArm=false; st.endArm=false; const r=$('#st'); if(r) r.dataset.pane='page'; stPage(); }
// the little value labels on the right of each row
function stValues(){
  const v=(id,t)=>{ const e=$('#stv-'+id); if(e) e.textContent=t; };
  v('display',(THEMES.find(t=>t.id===currentTheme)||{}).name||''); v('units',WU());
  v('plan',plan.hasPlan?`${plan.departureIcao||'----'} → ${plan.arrivalIcao||'----'}`:'None');
  v('flight',state.hasActiveFlight?(state.callsign||state.flightNumber||'Active'):'None');
  v('fmode',fm.mode&&FM_DOCK[fm.mode]?FM_DOCK[fm.mode].label:'Off');
  const a=acars.d||{}; v('datalink',a.hasLogon?(a.callsign||'Ready'):'Off');
  v('devices',st.devs?String(st.devs.devices.length):'');
  $$('#st .st-row,#st .st-me').forEach(b=>b.classList.toggle('on',b.dataset.sec===st.sec&&b.classList.contains('st-row')));
}
function stPage(soft){
  const el=$('#stPage'); if(!el) return;
  const it=ST_ALL.find(x=>x.id===st.sec);
  stValues();
  if(!it){ el.innerHTML=''; return; }
  const head=`<div class="st-ph"><button class="st-back" id="stBack"><svg viewBox="0 0 24 24"><path d="m15 18-6-6 6-6"/></svg>Settings</button><h2>${it.name}</h2></div>`;
  const page=({display:stDisplay,wallpaper:stWallpaper,units:stUnits,plan:stPlan,flight:stFlight,fmode:stFmode,datalink:stDatalink,devices:stDevices,about:stAbout}[it.id]);
  if(soft&&it.id==='devices'){ stLoadDevs(true); return; }
  el.innerHTML=head+`<div class="st-body">${page()}</div>`;
  $('#stBack').onclick=()=>{ st.pane='list'; $('#st').dataset.pane='list'; };
  ({display:stDisplayBind,wallpaper:stWallpaperBind,units:stUnitsBind,plan:stPlanBind,flight:stFlightBind,fmode:stFmodeBind,devices:stDevicesBind,datalink:stDatalinkBind}[it.id]||(()=>{}))();
}
const stCard=(h,foot='')=>`<div class="st-card">${h}</div>${foot?`<p class="st-foot">${foot}</p>`:''}`;
const stKv=(k,v)=>`<div class="st-kv"><span>${k}</span><b>${v}</b></div>`;

// ── Display ──
function stDisplay(){
  return `<div class="st-gl">Theme</div>`+stCard(`<div class="st-themes">${THEMES.map(t=>`<button class="st-theme${t.id===currentTheme?' on':''}" data-theme="${t.id}" aria-pressed="${t.id===currentTheme}">
      <span style="background:${t.c2}"><i style="background:${t.c1}"></i><i style="background:${t.c1}99"></i><i style="background:${t.c1}55"></i></span><b>${t.name}</b></button>`).join('')}</div>`,
    'The accent colour for the home screen and the lighter apps. Shared by every paired device.')
  +`<div class="st-gl">Appearance</div>`+stCard(`<div class="st-seg" role="radiogroup">${['dark','light'].map(m=>`<button data-mode="${m}" class="${currentMode===m?'on':''}" role="radio" aria-checked="${currentMode===m}">${m==='dark'?'Dark':'Light'}</button>`).join('')}</div>`,
    'This device only. Every app follows it. The DCDU screen in ACARS and the approach trace in the Landing report stay dark, like the instruments they copy.')
  +`<div class="st-gl">Interface size</div>`+stCard(`<div class="st-seg" role="radiogroup">${UI_SIZES.map(([v,l])=>{ const off=v!=='auto'&&+v/100>uiMax()+.001;
      return `<button data-ui="${v}" class="${uiSize===v?'on':''}" role="radio" aria-checked="${uiSize===v}"${off?' disabled':''}>${l}</button>`; }).join('')}</div>`,
    `This device only. Auto scales the EFB up on a large monitor so it reads well from the seat.${uiMax()<1.15?' Larger sizes need a wider screen.':''}`)
  +((document.documentElement.requestFullscreen||document.documentElement.webkitRequestFullscreen)?`<div class="st-gl">Screen</div>`+stCard(`<button class="st-btn" id="stFs">${document.fullscreenElement||document.webkitFullscreenElement?'Exit full screen':'Enter full screen'}</button>`):'');
}
function stDisplayBind(){
  $$('#stPage [data-theme]').forEach(b=>b.onclick=()=>{ setTheme(b.dataset.theme); stPage(); });
  $$('#stPage [data-mode]').forEach(b=>b.onclick=()=>{ setMode(b.dataset.mode); stPage(); });
  $$('#stPage [data-ui]').forEach(b=>b.onclick=()=>{ uiSet(b.dataset.ui); stPage(); });
  const fs=$('#stFs'); if(fs) fs.onclick=()=>{ $('#fullscreenBtn').click(); setTimeout(()=>{ if(st.sec==='display') stPage(); },300); };
}

// ── Flight mode ──
function stFmode(){
  const d=FM_DOCK[fm.mode], sw=(k,t,sub)=>`<label class="st-sw"><span><b>${t}</b><small>${sub}</small></span><input type="checkbox" data-fm="${k}"${fmPrefs[k]?' checked':''}></label>`;
  return stCard(`${stKv('Now',d?`${esc(d.label)}${fm.sterile?' <span class="warn">(sterile cockpit)</span>':''}`:'No flight')}
      ${stKv('Home dock',d?d.apps.map(id=>esc((APPS.find(a=>a.id===id)||{}).name||id)).join(', '):'Shown during a flight')}`,
    'The EFB follows the flight by itself: flight mode passing 1,000 ft AGL on the climb, approach mode below 1,000 ft on the way down, and arrival after touchdown. The home screen dock changes to the apps for each phase.')
   +`<div class="st-gl">This device</div>`+stCard(
      sw('alerts','Alerts','Banners for new messages, ACARS uplinks and flight-mode changes.')
     +sw('sterile','Sterile cockpit','Hold chat alerts while taxiing, below 10,000 ft and on the approach, then show them together. ACARS and CPDLC always come through.')
     +sw('awake','Keep screen on','Stops this device going to sleep during a flight. Starts after your next tap.'));
}
function stFmodeBind(){
  $$('#stPage [data-fm]').forEach(c=>c.onchange=()=>{ fmPrefs[c.dataset.fm]=c.checked; fmSave();
    if(c.dataset.fm==='sterile'&&!c.checked&&fm.held.length){ fm.sterile=false; fmRelease(); }
    fmTick(); stValues(); });
}

// ── Wallpaper ──
function stWallpaper(){
  return stCard(`<div class="st-wp"><div class="st-wpv"><div class="st-wpclock mono" id="stWpClock"></div><div class="st-wpicons">${'<i></i>'.repeat(8)}</div></div>
      <div class="st-wpa"><label class="st-btn pri">Choose photo<input id="wpFile" type="file" accept="image/*" hidden></label>
        <button class="st-btn" id="wpReset">Use default</button><span class="st-note" id="wpNote"></span></div></div>`,
    `Your photo is stored on the desktop app and shows on every paired device. ${wpUploaded?'':'The default is a sunset wing by Maria Sleptsova on Unsplash, free to use.'}`);
}
function stWallpaperBind(){
  const d=new Date(); $('#stWpClock').textContent=String(d.getHours()).padStart(2,'0')+':'+String(d.getMinutes()).padStart(2,'0');
  $('#wpFile').onchange=async e=>{ const file=e.target.files[0]; if(!file) return;
    if(file.size>12e6){ $('#wpNote').textContent='That photo is over 12 MB. Pick a smaller one.'; return; }
    $('#wpNote').textContent='Uploading…';
    try{ const buf=await file.arrayBuffer();
      await fetch('/efb/api/wallpaper',{method:'POST',headers:{'X-EFB-Token':token,'Content-Type':'application/octet-stream'},body:buf});
      await loadWallpaper(); stPage(); }catch{ $('#wpNote').textContent='Upload failed.'; } };
  $('#wpReset').onclick=async()=>{ await fetch('/efb/api/wallpaper',{method:'DELETE',headers:{'X-EFB-Token':token}}).catch(()=>{});
    await loadWallpaper(); stPage(); };
}

// ── Units ──
function stUnits(){
  const s=state||{}, fob=s.simConnected&&s.fuelLbs?s.fuelLbs:27337, tow=plan.takeoffWeightKg||64200, flow=s.simConnected&&s.fuelBurnPph?s.fuelBurnPph:5400;
  return `<div class="st-gl">Weight</div>`+stCard(`<div class="st-seg big" role="radiogroup">${[['KG','Kilograms','kg and tonnes'],['LB','Pounds','lb and 1000 lb']].map(([k,l,sub])=>
      `<button data-wu="${k}" class="${WU()===k?'on':''}" role="radio" aria-checked="${WU()===k}"><b>${l}</b><small>${sub}</small></button>`).join('')}</div>
    <div class="st-sample mono"><span><small>FOB</small>${fmtW(fob)}</span><span><small>FLOW</small>${fmtW(flow,false)} ${WU()==='LB'?'PPH':'KG/H'}</span>
      <span><small>TOW</small>${bigFromKg(tow).toFixed(1)} ${BWU()}</span></div>`,
    'One unit for every app on every paired device: the dashboard, performance, Pilot Brief, the Landing Report and the scratchpad hints. The SimBrief OFP itself and each aircraft\'s printed checklist keep their own units, like the paper versions.');
}
function stUnitsBind(){
  $$('#stPage [data-wu]').forEach(b=>b.onclick=async()=>{ applyPrefs({weightUnit:b.dataset.wu}); stPage();
    try{ applyPrefs(await postJSON('/efb/api/prefs',{weightUnit:b.dataset.wu})); }catch{} stPage(); });
}

// ── Flight plan ──
function stPlan(){
  return stCard(plan.hasPlan?`${stKv('Route',`${esc(plan.departureIcao||'----')} → ${esc(plan.arrivalIcao||'----')}`)}${stKv('Aircraft',esc(plan.aircraftType||'—'))}${stKv('Source',plan.fromEfbImport?'Imported in the EFB':'From the desktop app (ACARS)')}`
      :`<div class="st-empty">No flight plan loaded. A plan imported in the desktop app's ACARS page appears here automatically.</div>`)
   +`<div class="st-gl">Import from SimBrief</div>`+stCard(`<div class="st-inrow"><input id="sbUser" placeholder="SimBrief username or pilot ID" autocomplete="off" autocapitalize="off" spellcheck="false">
      <button class="st-btn pri" id="sbImport">Import</button></div><div class="st-err hidden" id="sbErr" role="alert"></div>`,
    'Fills the EFB only (Pilot Brief, performance, Weather). It doesn\'t change your ACARS flight on the desktop.');
}
function stPlanBind(){
  const go=async()=>{ const u=$('#sbUser').value.trim(), b=$('#sbImport'), e=$('#sbErr');
    if(!u){ $('#sbUser').focus(); return; }
    b.disabled=true; b.textContent='Importing…'; e.classList.add('hidden');
    let r; try{ r=await postJSON('/efb/api/flightplan/import',{username:u}); }catch{ r={error:'Could not reach the desktop app.'}; }
    b.disabled=false; b.textContent='Import';
    if(!r||r.error){ e.textContent=(r&&r.error)||'Import failed.'; e.classList.remove('hidden'); } else { plan=r; stPage(); } };
  $('#sbImport').onclick=go; $('#sbUser').onkeydown=e=>{ if(e.key==='Enter') go(); };
}

// ── Active flight ──
function stFlight(){
  const s=state||{};
  return stCard(s.hasActiveFlight?`${stKv('Flight',esc(s.callsign||s.flightNumber||'—'))}${stKv('Route',`${esc(s.departureIcao||'----')} → ${esc(s.arrivalIcao||'----')}`)}
      ${stKv('Phase',esc(s.phase&&s.phase!=='Unknown'?s.phase:'—'))}${stKv('Aircraft',esc(s.aircraftType||'—'))}`
      :`<div class="st-empty">No flight in progress.</div>`)
   +(s.hasActiveFlight?stCard(`<button class="st-btn danger${st.endArm?' arm':''}" id="endFlight">${st.endArm?'Tap again to end the flight':'End flight'}</button>`,
      'Ends the flight and files the PIREP, the same as the desktop button.'):'');
}
function stFlightBind(){
  const b=$('#endFlight'); if(!b) return;
  b.onclick=async()=>{ if(!st.endArm){ st.endArm=true; stPage(); setTimeout(()=>{ if(st.endArm){ st.endArm=false; if(st.sec==='flight') stPage(); } },4000); return; }
    st.endArm=false; await postJSON('/efb/api/flight/end',{}).catch(()=>{}); stPage(); };
}

// ── Datalink ──
function stDatalink(){
  const a=acars.d||{};
  return stCard(`${stKv('Hoppie logon code',a.hasLogon?'<span class="ok">Set</span>':'<span class="warn">Not set</span>')}${stKv('Callsign',esc(a.callsign||'—'))}
      ${stKv('CPDLC',a.cpdlcState==='connected'?esc(a.cpdlcStation)+' · connected':a.cpdlcState==='pending'?esc(a.cpdlcStation)+' · logon sent':'Not logged on')}
      ${stKv('Last inbox check',a.lastPollUtc?fmtT(a.lastPollUtc)+(a.lastPollOk?'':' · failed'):'—')}
      ${stKv('Automatic company messages',a.autoOpsEnabled?'On':'Off')}`,
    'The logon code and the automatic messages (loadsheet, reminders, advisories) are set in the desktop app under Settings › Hoppies ACARS. Get a free logon code at hoppie.nl/acars.')
   +stCard(`<button class="st-btn" id="stOpenAcars">Open ACARS</button>`);
}
function stDatalinkBind(){ $('#stOpenAcars').onclick=()=>openApp('acars'); }

// ── Devices ──
function stDevices(){
  const d=st.devs;
  const list=!d?'<div class="st-empty">Loading…</div>':d.devices.length?d.devices.map(x=>`<div class="st-dev${x.id===mySocketId?' me':''}">
      <span class="st-dvi"><svg viewBox="0 0 24 24">${/iPhone|phone/i.test(x.name)?'<rect x="7" y="3" width="10" height="18" rx="2"/><path d="M11 18h2"/>':/PC|Mac|Chromebook/i.test(x.name)?'<rect x="3" y="5" width="18" height="12" rx="1.5"/><path d="M8 20h8"/>':'<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M11 18h2"/>'}</svg></span>
      <span><b>${esc(x.name)}${x.id===mySocketId?' <em>This device</em>':''}</b><small class="mono">${esc(x.ip)}, connected ${fmtT(x.connectedUtc)}</small></span></div>`).join('')
    :'<div class="st-empty">No devices connected live (this one may be on REST polling).</div>';
  return `<div class="st-gl">Connected now</div>`+stCard(list)
   +`<div class="st-gl">Pair another device</div>`+stCard(`<div class="st-pair"><div><small>Pairing code</small><b class="mono">${d?esc(d.pairingCode.replace(/(\d{3})(\d{3})/,'$1 $2')):'—'}</b></div>
       <div><small>Open on the same Wi-Fi</small><b class="mono">${d?esc(d.url||location.origin):'—'}</b></div></div>`,
     'Open the address on the new device and enter the code. It is also shown in the desktop app.')
   +`<div class="st-gl">Pairing</div>`+stCard(`<button class="st-btn" id="stForget">Forget this device</button>
       <button class="st-btn danger${st.rmArm?' arm':''}" id="stUnpair">${st.rmArm?'Tap again: every device must pair again':'Unpair all devices'}</button>`,
     'Unpairing makes a new pairing code. Every device, this one included, has to pair again.');
}
async function stLoadDevs(soft){
  try{ const d=await getJSON('/efb/api/devices'); if(d&&Array.isArray(d.devices)) st.devs=d; }catch{}
  if(st.sec!=='devices') { stValues(); return; }
  if(soft){ const sel=document.activeElement&&document.activeElement.id; stPage(); if(sel&&$('#'+sel)) $('#'+sel).focus(); }
  else stPage();
}
function stDevicesBind(){
  if(!st.devs) stLoadDevs();
  $('#stForget').onclick=()=>{ try{ localStorage.removeItem('efbToken'); }catch{} token=''; try{ ws&&ws.close(); }catch{} location.reload(); };
  $('#stUnpair').onclick=async()=>{
    if(!st.rmArm){ st.rmArm=true; stPage(); setTimeout(()=>{ if(st.rmArm){ st.rmArm=false; if(st.sec==='devices') stPage(); } },4000); return; }
    st.rmArm=false; await postJSON('/efb/api/pairing/reset',{}).catch(()=>{});
    try{ localStorage.removeItem('efbToken'); }catch{} token=''; location.reload(); };
}

// ── About ──
function stAbout(){
  const live=ws&&ws.readyState===1;
  return `<div class="st-hero"><img src="mark-a.png" alt="" onerror="this.remove()"><b>AviatesAir EFB</b><small>${hello.version?'Version '+esc(hello.version):''}</small></div>`
   +stCard(`${stKv('Pilot',esc(msg.myPilotName||hello.pilotName||'—'))}${stKv('Airline',esc(hello.airline||'AviatesAir'))}
      ${stKv('Host',`<span class="mono">${esc(location.host)}</span>`)}${stKv('Connection',live?'<span class="ok">Live (WebSocket)</span>':pollOk?'<span class="warn">Polling</span>':'<span class="bad">Offline</span>')}
      ${stKv('Weight unit',WU()==='LB'?'Pounds':'Kilograms')}`)
   +`<div class="st-gl">Data</div>`+stCard(`${stKv('Weather','aviationweather.gov')}${stKv('Online ATC and traffic','VATSIM and IVAO')}${stKv('Datalink','Hoppie ACARS')}
      ${stKv('Radar','RainViewer')}${stKv('Map tiles','Esri')}${stKv('Flight plans','SimBrief')}`)
   +`<p class="st-foot center">For flight simulation only. Performance figures are training-grade and nothing here is for real-world navigation or operational use.</p>`;
}

// ═══ INTERFACE SIZE ════════════════════════════════════════
// The layout is designed for a 10–13" tablet. On a big monitor (a second screen next to the sim)
// "Auto" scales the whole device up so text stays readable from the seat; each device can also pick
// a fixed size. Applied as CSS zoom on #app, which keeps the layout fluid at the scaled width.
const UI_KEY='efbUiSize', UI_SIZES=[['auto','Auto'],['90','90%'],['100','100%'],['115','115%'],['130','130%'],['150','150%']];
let uiSize=(()=>{ try{ return localStorage.getItem(UI_KEY)||'auto'; }catch{ return 'auto'; } })();
function uiTarget(){
  const w=innerWidth, h=innerHeight;
  const z=uiSize==='auto'?(w<1400?1:Math.min(1.6,w/1280,h/860)):(+uiSize/100||1);
  return Math.round(Math.max(.8,Math.min(z,uiMax()))*100)/100;
}
// Breakpoints follow the real screen, not the zoomed layout, so scaling up stops while the layout
// still has the ~1,100 px a tablet landscape gets. A phone or tablet can only go smaller.
const uiMax=()=>Math.max(1,innerWidth/1100);
function uiApply(){ const a=$('#app'); if(!a) return; const z=uiTarget(); a.style.zoom=z===1?'':String(z); }
const uiZ=()=>parseFloat(($('#app')||{style:{}}).style.zoom)||1;
// rendered px per CSS px for an element (zoom and any running transform), for pointer maths
function uiScaleOf(el){ const w=el&&el.offsetWidth; return w?el.getBoundingClientRect().width/w:1; }
function uiSet(v){ uiSize=v; try{ localStorage.setItem(UI_KEY,v); }catch{} uiApply(); }
let uiT=0; window.addEventListener('resize',()=>{ clearTimeout(uiT); uiT=setTimeout(uiApply,120); });

// ═══ ALERTS ════════════════════════════════════════════════
// iPad-style banners for things that arrive while you're elsewhere: a new message, an ACARS
// uplink, a CPDLC instruction, and the flight-mode changes below. Tap opens the app, the cross
// dismisses, and they clear by themselves (an open CPDLC instruction stays longer).
const ntSeen=new Set();
function notify(o){
  if(!fmPrefs.alerts&&!o.always) return;
  const stack=$('#ntStack'); if(!stack) return;
  if(o.key) stack.querySelectorAll('.nt').forEach(x=>{ if(x.dataset.key===o.key) x.remove(); });
  const a=APPS.find(x=>x.id===o.app);
  const el=document.createElement('div'); el.className='nt'+(o.tone?' '+o.tone:''); el.dataset.key=o.key||''; el.setAttribute('role','status');
  el.innerHTML=`<button class="nt-main" type="button">${a?`<span class="nt-ic" data-app="${a.id}"><svg ${SVG_ATTRS}>${ICONS[a.icon]}</svg></span>`:''}
    <span class="nt-tx"><span class="nt-hd"><b>${esc(o.title)}</b><small>${esc(o.meta||(a?a.name:''))}</small></span>
    <span class="nt-bd">${esc(o.text||'')}</span></span></button>
    <button class="nt-x" type="button" aria-label="Dismiss"><svg viewBox="0 0 24 24"><path d="M7 7l10 10M17 7 7 17"/></svg></button>`;
  let t=0; const bye=()=>{ clearTimeout(t); el.classList.add('out'); setTimeout(()=>el.remove(),180); };
  const arm=()=>{ clearTimeout(t); t=setTimeout(bye,o.ttl||6000); };
  el.querySelector('.nt-main').onclick=()=>{ bye(); if(o.onTap) o.onTap(); if(o.app) openApp(o.app); };
  el.querySelector('.nt-x').onclick=bye;
  el.onpointerenter=()=>clearTimeout(t); el.onpointerleave=arm;
  stack.prepend(el); arm();
  [...stack.children].slice(3).forEach(x=>x.remove());              // three at most, newest on top
}
const ntFresh=iso=>{ const t=+new Date(iso); return !isNaN(t)&&Date.now()-t<10*60e3; };
// called with the previous messaging state each time the host pushes a new one
function ntMessages(old){
  const before=new Set(((old&&old.messages)||[]).map(m=>m.id));
  for(const m of msg.messages||[]){
    if(m.mine||m.isRead||before.has(m.id)||ntSeen.has(m.id)||!ntFresh(m.sentAt)) continue;
    ntSeen.add(m.id);
    const crew=m.type==='Broadcast', conv=crew?MS_CREW:m.senderId;
    if(activeApp==='messages'&&(ms.sel===conv||!matchMedia('(min-width:761px)').matches)&&!document.hidden) continue;  // already looking at it
    const n={key:'ms-'+conv, app:'messages', title:crew?'Crew Room':(m.senderName||'Pilot'), meta:crew?(m.senderName||''):'Message',
      text:m.content, onTap:()=>{ ms.sel=conv; }};
    if(fm.sterile){ fm.held.push(n); fmChrome(); } else notify(n);
  }
}
function ntAcars(old){
  const before=new Set(((old&&old.messages)||[]).map(m=>m.id));
  for(const m of (acars.d&&acars.d.messages)||[]){
    if(m.dir==='out'||m.read||before.has(m.id)||ntSeen.has(m.id)||!ntFresh(m.utc)) continue;
    ntSeen.add(m.id);
    if(activeApp==='acars'&&!document.hidden) continue;
    const cp=m.type==='cpdlc', ask=cp&&m.open&&(m.responses||[]).length;
    // operational traffic is never held for sterile cockpit: it's the crew's job to read it
    notify({key:'ac-'+m.id, app:'acars', tone:ask?'warn':'', ttl:ask?20000:8000,
      title:cp?`${m.station||'ATC'} CPDLC`:m.kind==='COMPANY'||m.station==='VAOPS'?'Company':(m.station||'ACARS'),
      meta:ask?'Response required':cp?'CPDLC':'ACARS', text:String(m.text||'').split('\n').slice(0,2).join('  '),
      onTap:()=>{ acars.folder='all'; acars.sel=m.id; }});
  }
}

// ═══ FLIGHT MODE ═══════════════════════════════════════════
// The EFB follows the flight by itself. Passing 1,000 ft AGL on the climb it switches to flight
// mode; below 1,000 ft on the approach to approach mode; after touchdown to arrival. Each change
// moves the home dock to the apps for that phase and shows a short banner. With sterile cockpit on,
// chat (not ACARS) is held during taxi, below 10,000 ft and on the approach, then summarised.
// A 300 ft band stops a level-off near 1,000 ft from flapping between modes.
const FM_KEY='efbFlightMode';
let fmPrefs={alerts:true, sterile:true, awake:true};
try{ Object.assign(fmPrefs,JSON.parse(localStorage.getItem(FM_KEY)||'{}')); }catch{}
function fmSave(){ try{ localStorage.setItem(FM_KEY,JSON.stringify(fmPrefs)); }catch{} }
let fm={mode:null, sterile:false, held:[]};
const FM_DOCK={
  ground:  {label:'Before departure', apps:['ofp','depperf','atis','checklist','acars']},
  departure:{label:'Departure',       apps:['checklist','map','acars','depperf','atis']},
  flight:  {label:'In flight',        apps:['map','atis','acars','arrperf','checklist']},
  approach:{label:'Approach',         apps:['checklist','arrperf','atis','map','acars']},
  landed:  {label:'After landing',    apps:['landing','checklist','acars','messages','flight']},
};
function fmDerive(s,cur){
  if(!s||!(s.hasActiveFlight||s.sessionState==='Landed'||s.sessionState==='Complete')) return 'idle';
  const p=s.phase||'', agl=+s.altitudeAglFt||0, vs=+s.verticalSpeedFpm||0;
  const air=/^(InitialClimb|Climb|Cruise|TopOfDescent|Descent|Approach|FinalApproach)$/.test(p)||agl>150;
  const landed=!!s.landingUtc||/^(Rollout|Vacating)$/.test(p)||s.sessionState==='Landed'||s.sessionState==='Complete';
  if(landed&&agl<150) return 'landed';
  if(!air) return 'ground';
  if(cur==='flight') return agl<1000&&vs<-100&&(/Approach/.test(p)||!(s.distanceRemainingNm>30))?'approach':'flight';
  if(cur==='approach') return agl>1300?'flight':'approach';
  if(cur==='departure') return agl>=1000?'flight':'departure';
  // first sight (the page opened mid-flight) or just airborne off the ground
  return agl>=1000?'flight':(vs<-300||/Approach|Descent/.test(p))?'approach':'departure';
}
function fmIsSterile(s,mode){
  if(!fmPrefs.sterile) return false;
  const p=s.phase||'';
  return mode==='departure'||mode==='approach'
    ||(mode==='ground'&&/^(Pushback|Taxi|Takeoff)$/.test(p))
    ||(mode==='flight'&&(+s.altitudeFt||0)<10000)
    ||(mode==='landed'&&/^(Landing|Rollout|Vacating|Taxi)$/.test(p));
}
const fmFt=n=>Math.round(n).toLocaleString('en-US');
function fmTick(){
  const s=state||{}, prev=fm.mode, mode=fmDerive(s,prev);
  fm.mode=mode;
  if(prev!==null&&prev!==mode){
    const arrive=s.arrivalName||s.arrivalIcao||'';
    if(mode==='flight'&&prev==='departure')
      notify({key:'fm', app:'map', title:'Flight mode', meta:`${fmFt(s.altitudeAglFt||1000)} ft AGL`,
        text:fmPrefs.sterile?'Passing 1,000 ft. Chat is held until 10,000 ft.':'Passing 1,000 ft. Map and en-route apps are on the dock.'});
    else if(mode==='flight'&&prev==='approach')
      notify({key:'fm', app:'map', tone:'warn', title:'Go-around', meta:`${fmFt(s.altitudeAglFt||0)} ft AGL`, text:'Climbing away. Flight mode is back on.'});
    else if(mode==='approach')
      notify({key:'fm', app:'checklist', title:'Approach', meta:'Below 1,000 ft AGL', text:fmPrefs.sterile?'Chat alerts are held until you land.':'Approach and landing apps are on the dock.'});
    else if(mode==='landed'&&prev!=='idle'&&prev!=='ground')
      notify({key:'fm', app:'landing', title:'Landed', meta:s.arrivalIcao||'', ttl:12000,
        text:`${arrive?'Welcome to '+arrive+'. ':''}Your landing report is ready.`});
  }
  const sterile=mode!=='idle'&&fmIsSterile(s,mode);
  if(fm.sterile&&!sterile&&fm.held.length) fmRelease();
  fm.sterile=sterile;
  kaWant(fmPrefs.awake&&mode!=='idle'&&s.sessionState!=='Complete'&&!s.blockInUtc);   // until on blocks at the gate
  fmChrome();
}
function fmRelease(){
  const held=fm.held; fm.held=[];
  if(held.length===1){ notify(held[0]); return; }
  const who=[...new Set(held.map(n=>n.title))];
  notify({key:'fm-held', app:'messages', ttl:9000, title:`${held.length} messages`, meta:'Held during sterile cockpit',
    text:who.length<=3?who.join(', '):`${who.slice(0,2).join(', ')} and ${who.length-2} more`});
}
// menu-bar chip + home dock
let fmDockFor='';
function fmChrome(){
  const chip=$('#mbFm');
  if(chip){ chip.classList.toggle('hidden',!fm.sterile); chip.textContent=fm.held.length?`STERILE ${fm.held.length}`:'STERILE'; }
  const dock=$('#homeDock'), d=FM_DOCK[fm.mode];
  $('#home')?.classList.toggle('has-dock',!!d);
  if(!dock) return;
  dock.classList.toggle('hidden',!d);
  if(!d||fmDockFor===fm.mode) return;
  fmDockFor=fm.mode;
  dock.innerHTML=`<span class="dk-lbl">${d.label}</span><span class="dk-apps">${d.apps.map(id=>APPS.find(a=>a.id===id)).filter(Boolean).map(a=>
    `<button class="dk-app" data-app="${a.id}" aria-label="${esc(a.name)}"><span class="tile">${tileSvg(a.id)}</span><span class="badge hidden" data-badge="${a.id}"></span><span class="dk-n">${esc(a.name)}</span></button>`).join('')}</span>`;
  dock.querySelectorAll('[data-app]').forEach(b=>b.onclick=()=>openApp(b.dataset.app));
  renderBadges();
}
function fmInit(){
  for(const m of msg.messages||[]) ntSeen.add(m.id);               // what's already there isn't news
  for(const m of (acars.d&&acars.d.messages)||[]) ntSeen.add(m.id);
  fmTick();
}

// ═══ KEEP AWAKE ════════════════════════════════════════════
// The screen stays on during a flight. The Wake Lock API needs HTTPS, which a LAN address isn't,
// so on iPad/Android it falls back to a silent one-pixel looping video (the long-standing NoSleep
// approach). Both need a tap first; the next tap anywhere arms it. Released when the flight ends.
const KA_SRC='data:video/mp4;base64,AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAZWbW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAAD6AAAQAAAQAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAAAqN0cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAABAAAAAAAAD6AAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAABAAAAAQAAAAAAAkZWR0cwAAABxlbHN0AAAAAAAAAAEAAA+gAACAAAABAAAAAAIbbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAABAAAABQABVxAAAAAAALWhkbHIAAAAAAAAAAHZpZGUAAAAAAAAAAAAAAABWaWRlb0hhbmRsZXIAAAABxm1pbmYAAAAUdm1oZAAAAAEAAAAAAAAAAAAAACRkaW5mAAAAHGRyZWYAAAAAAAAAAQAAAAx1cmwgAAAAAQAAAYZzdGJsAAAAwnN0c2QAAAAAAAAAAQAAALJhdmMxAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAABAAEABIAAAASAAAAAAAAAABFUxhdmM2Mi4yOC4xMDIgbGlieDI2NAAAAAAAAAAAAAAAGP//AAAAOGF2Y0MBZAAK/+EAGWdkAAqscgRewEQAAAMABAAAAwAIPEiWEYABAAho6EOBlEyEwP34+AAAAAAQcGFzcAAAAAEAAAABAAAAFGJ0cnQAAAAAAAAF4gAAAAAAAAAYc3R0cwAAAAAAAAABAAAABAAAQAAAAAAUc3RzcwAAAAAAAAABAAAAAQAAAChjdHRzAAAAAAAAAAMAAAABAACAAAAAAAEAAQAAAAAAAgAAQAAAAAAoc3RzYwAAAAAAAAACAAAAAQAAAAIAAAABAAAAAgAAAAEAAAABAAAAJHN0c3oAAAAAAAAAAAAAAAQAAALHAAAADgAAAA4AAAAOAAAAHHN0Y28AAAAAAAAAAwAABoYAAAlwAAAJngAAAt10cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAACAAAAAAAAD6AAAAAAAAAAAAAAAAEBAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAkZWR0cwAAABxlbHN0AAAAAAAAAAEAAA+gAAAEAAABAAAAAAJVbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAAfQAAAgQBVxAAAAAAALWhkbHIAAAAAAAAAAHNvdW4AAAAAAAAAAAAAAABTb3VuZEhhbmRsZXIAAAACAG1pbmYAAAAQc21oZAAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAABxHN0YmwAAAB+c3RzZAAAAAAAAAABAAAAbm1wNGEAAAAAAAAAAQAAAAAAAAAAAAEAEAAAAAAfQAAAAAAANmVzZHMAAAAAA4CAgCUAAgAEgICAF0AVAAAAAAAfQAAAASAFgICABRWIVuUABoCAgAECAAAAFGJ0cnQAAAAAAAAfQAAAASAAAAAgc3R0cwAAAAAAAAACAAAAIAAABAAAAAABAAABAAAAADRzdHNjAAAAAAAAAAMAAAABAAAAAQAAAAEAAAACAAAACAAAAAEAAAADAAAAGAAAAAEAAACYc3RzegAAAAAAAAAAAAAAIQAAABUAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAAAQAAAAEAAAABAAAABxzdGNvAAAAAAAAAAMAAAlbAAAJfgAACawAAAAac2dwZAEAAAByb2xsAAAAAgAAAAH//wAAABxzYmdwAAAAAHJvbGwAAAABAAAAIQAAAAEAAABidWR0YQAAAFptZXRhAAAAAAAAACFoZGxyAAAAAAAAAABtZGlyYXBwbAAAAAAAAAAAAAAAAC1pbHN0AAAAJal0b28AAAAdZGF0YQAAAAEAAAAATGF2ZjYyLjEyLjEwMgAAAAhmcmVlAAADjm1kYXQAAAKxBgX//63cRem95tlIt5Ys2CDZI+7veDI2NCAtIGNvcmUgMTY1IHIzMjIzIDA0ODBjYjAgLSBILjI2NC9NUEVHLTQgQVZDIGNvZGVjIC0gQ29weWxlZnQgMjAwMy0yMDI1IC0gaHR0cDovL3d3dy52aWRlb2xhbi5vcmcveDI2NC5odG1sIC0gb3B0aW9uczogY2FiYWM9MSByZWY9MTYgZGVibG9jaz0xOi0zOi0zIGFuYWx5c2U9MHgzOjB4MTMzIG1lPXVtaCBzdWJtZT0xMCBwc3k9MSBwc3lfcmQ9Mi4wMDowLjcwIG1peGVkX3JlZj0xIG1lX3JhbmdlPTI0IGNocm9tYV9tZT0xIHRyZWxsaXM9MiA4eDhkY3Q9MSBjcW09MCBkZWFkem9uZT0yMSwxMSBmYXN0X3Bza2lwPTEgY2hyb21hX3FwX29mZnNldD0tNCB0aHJlYWRzPTEgbG9va2FoZWFkX3RocmVhZHM9MSBzbGljZWRfdGhyZWFkcz0wIG5yPTAgZGVjaW1hdGU9MSBpbnRlcmxhY2VkPTAgYmx1cmF5X2NvbXBhdD0wIGNvbnN0cmFpbmVkX2ludHJhPTAgYmZyYW1lcz04IGJfcHlyYW1pZD0yIGJfYWRhcHQ9MiBiX2JpYXM9MCBkaXJlY3Q9MyB3ZWlnaHRiPTEgb3Blbl9nb3A9MCB3ZWlnaHRwPTIga2V5aW50PTI1MCBrZXlpbnRfbWluPTEgc2NlbmVjdXQ9NDAgaW50cmFfcmVmcmVzaD0wIHJjX2xvb2thaGVhZD02MCByYz1jcmYgbWJ0cmVlPTEgY3JmPTUxLjAgcWNvbXA9MC42MCBxcG1pbj0wIHFwbWF4PTY5IHFwc3RlcD00IGlwX3JhdGlvPTEuNDAgYXE9MToxLjIwAIAAAAAOZYiBAANZz/7KT4FLnWcAAAAKQZoIbYgxnP+rgN4CAExhdmM2Mi4yOC4xMDIAAjBADgAAAApBnhAnEGM5/+WBARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcAAAAKAZ4YTUgxnP/lgAEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBwEYIAcBGCAHARggBw==';
let ka={want:false, lock:null, video:null};
function kaWant(w){ if(w===ka.want) return; ka.want=w; w?kaArm():kaRelease(); }
async function kaArm(){
  if(!ka.want||document.hidden) return;
  if('wakeLock' in navigator&&window.isSecureContext){
    if(ka.lock) return;
    try{ ka.lock=await navigator.wakeLock.request('screen'); ka.lock.addEventListener('release',()=>{ ka.lock=null; }); return; }catch{}
  }
  if(!ka.video){ const v=document.createElement('video'); v.className='ka-video'; v.muted=true; v.loop=true;
    v.setAttribute('muted',''); v.setAttribute('playsinline',''); v.setAttribute('aria-hidden','true'); v.src=KA_SRC;
    document.body.appendChild(v); ka.video=v; }
  if(ka.video.paused) ka.video.play().catch(()=>{});
}
function kaRelease(){ try{ ka.lock&&ka.lock.release(); }catch{} ka.lock=null; if(ka.video) ka.video.pause(); }
document.addEventListener('visibilitychange',()=>{ if(!document.hidden) kaArm(); });
document.addEventListener('pointerdown',()=>{ if(ka.want&&!ka.lock&&(!ka.video||ka.video.paused)) kaArm(); },{passive:true,capture:true});
// read-only snapshot for the dev tools and the audit scripts
window.efbDebug=()=>({mode:fm.mode, sterile:fm.sterile, held:fm.held.length, keepAwake:ka.want, zoom:uiZ(), phase:state.phase, agl:state.altitudeAglFt});

// ── theme / mode ──
let currentTheme='aviates',currentMode='dark';
function loadPrefs(){
  currentTheme=localStorage.getItem('efbTheme')||'aviates'; currentMode=localStorage.getItem('efbMode')||'dark';
  document.documentElement.dataset.theme=currentTheme; document.documentElement.dataset.mode=currentMode;
  getJSON('/efb/api/theme').then(d=>{if(d.theme&&d.theme!==currentTheme)setTheme(d.theme);}).catch(()=>{});
}
function setTheme(id){ currentTheme=id; document.documentElement.dataset.theme=id; localStorage.setItem('efbTheme',id); postJSON('/efb/api/theme',{theme:id}).catch(()=>{}); }
function setMode(m){ currentMode=m; document.documentElement.dataset.mode=m; localStorage.setItem('efbMode',m); }

// go!
// ?print=<manual|doc id|ALL> renders the PDF edition of the manuals instead of the tablet
const PRINT=new URLSearchParams(location.search).get('print');
if(PRINT) printBook(PRINT); else if(!token)showPairing(); else boot();
})();
