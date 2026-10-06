const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
for(const version of ['4.9.5.0','4.10.0.40']){
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation', 'web-'+version+'.js'),'utf8');
 new vm.Script(src); // Parse the complete exact transformed module, not a synthetic anchor.
 const a=src.indexOf('function strmBridgeCandidate('),b=src.indexOf('function renderTracksEventsNative(',a);
 const imports=[],calls=[],selected=[];let fallback=0;
 const scope={window:{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1},Emby:{importModule:async p=>{imports.push(p);return{render:async o=>calls.push(o)}}},
 _browser:{default:{}},_htmlmediahelper:{default:{enableHlsJsPlayer:()=>true}},
 _connectionmanager:{default:{getApiClient:()=>({})}},renderTracksEventsNative:()=>fallback++,removeCueEvents:()=>{},setTrackForCustomDisplay:(i,v,t)=>selected.push(t)};
 vm.createContext(scope);vm.runInContext(src.slice(src.indexOf('function strmBridgeHlsCapable('),src.indexOf('function strmBridgeInstallProfile('))+src.slice(a,b),scope);
 const selection=src.indexOf('function setCurrentTrackElement('),next=src.indexOf('function startInitialSubtitleTrackTimeout(',selection);
 assert.ok(selection>0&&next>selection);
 vm.runInContext(src.slice(selection,next),scope);
 (async()=>{
 const instance={};const track={Codec:'ass',IsExternal:false,DeliveryMethod:'External'};const media={Container:'mkv',Protocol:'Http'};
 await scope.renderTracksEvents(instance,{},track,{},media);
 assert.equal(calls.length,1);assert.equal(fallback,0);
 await scope.renderTracksEvents(instance,{}, {...track,IsExternal:true},{},media);assert.equal(fallback,1);
 await scope.renderTracksEvents(instance,{}, {...track,Codec:'subrip'},{},media);assert.equal(calls.length,2);
 await scope.renderTracksEvents(instance,{},track,{}, {...media,Container:'m2ts'});assert.equal(fallback,2);
 scope._htmlmediahelper.default.enableHlsJsPlayer=()=>false;
 await scope.renderTracksEvents(instance,{},track,{},media);assert.equal(fallback,3);assert.equal(calls.length,2);
 scope._htmlmediahelper.default.enableHlsJsPlayer=()=>true;scope._browser.default.chromecast=true;
 await scope.renderTracksEvents(instance,{},track,{},media);assert.equal(fallback,4);assert.equal(calls.length,2);
 scope._browser.default.chromecast=false;
 let runtime;scope._htmlmediahelper.default.enableHlsJsPlayer=(value,type)=>{runtime=value;assert.equal(type,'Video');return true;};
 assert.equal(scope.strmBridgeCandidate(track,{...media,RunTimeTicks:123}),true);assert.equal(runtime,123);
 const subrip={...track,Codec:'subrip',Type:'Subtitle',Index:3};
 const element={textTracks:[{mode:'showing'},{mode:'showing'}]};
 scope.setCurrentTrackElement({setSubtitleOffset:()=>{}},element,3,{mediaSource:{...media,MediaStreams:[subrip]}});
 assert.equal(selected[0],subrip);assert.ok(element.textTracks.every(t=>t.mode==='disabled'));
 console.log('PASS transformed Web module syntax, automatic ASS/SubRip dispatch and native exclusion '+version);
 })().catch(e=>{console.error(e);process.exitCode=1});
}

// Exercise the real host's manual selection and delayed startup selection together.
// A pending startup choice must not remove the newly selected bridge subtitle.
async function checkStartupSelection(version) {
 const path=require('node:path');
 const src=fs.readFileSync(path.join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 let disposed=0,starts=0;
 const scope={window:{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1},console:{log(){}},
   isNativeLG:false,_browser:{default:{}},_htmlmediahelper:{default:{enableHlsJsPlayer:()=>true}},sortMediaStreamTextTracks:()=>0,getMediaStreamSubtitleTracks:s=>s.MediaStreams,
   removeCueEvents:()=>{},setCueAppearance:()=>{},enableChunkedResponse:()=>false,
   _connectionmanager:{default:{getApiClient:()=>({})}},
   Emby:{importModule:async()=>({render:async o=>{starts++;o.instance.strmBridgeSubtitle={dispose(){disposed++;}};}})},
   renderTracksEventsNative:()=>{},subtitleTrackIndexToSetOnPlaying:-1,initialSubtitleTrackTimeout:null,clearTimeout,
   HtmlVideoPlayer:function(){}};
 vm.createContext(scope);
 const wrapper=src.indexOf('function strmBridgeCandidate('),native=src.indexOf('function renderTracksEventsNative(',wrapper);
 vm.runInContext(src.slice(src.indexOf('function strmBridgeHlsCapable('),src.indexOf('function strmBridgeInstallProfile('))+src.slice(wrapper,native),scope);
 const destruction=src.indexOf('function destroyCustomTrack(');
 vm.runInContext(src.slice(destruction,src.indexOf('function ',destruction+10)),scope);
 const custom=src.indexOf('function setTrackForCustomDisplay('),timer=src.indexOf('function startInitialSubtitleTrackTimeout(',custom);
 vm.runInContext(src.slice(custom,timer),scope);
 const track={Index:0,Type:'Subtitle',Codec:'ass',IsExternal:false,DeliveryMethod:'External'};
 const instance={_mediaElement:{textTracks:[]},customTrackIndex:-1,setSubtitleOffset:()=>{},
   _currentPlayOptions:{item:{Id:'fixture'},mediaSource:{Id:'source',Container:'mkv',Protocol:'Http',MediaStreams:[track]}}};
 scope.self=instance;
 const setter=version.startsWith('4.9')?'self.setSubtitleStreamIndex=function(index){':'HtmlVideoPlayer.prototype.setSubtitleStreamIndex=function(index){';
 const at=src.indexOf(setter);assert.ok(at>=0);
 vm.runInContext(src.slice(at,src.indexOf('},',at)+1)+';',scope);
 const manual=version.startsWith('4.9')?instance.setSubtitleStreamIndex:scope.HtmlVideoPlayer.prototype.setSubtitleStreamIndex;
 manual.call(instance,0);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(starts,1);
 const bound='this.boundonInitialSubtitleTrackTimeout=';const callbackAt=src.indexOf(bound)+bound.length;
 const callbackEnd=src.indexOf('}.bind(this)',callbackAt)+1;
 const callback=vm.runInContext('('+src.slice(callbackAt,callbackEnd)+')',scope);
 callback.call(instance);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(disposed,0,'delayed startup selection removed the newly selected subtitle');
 assert.equal(instance.customTrackIndex,0);
 manual.call(instance,-1);
 assert.equal(disposed,1,'explicit subtitle off must still dispose');
 callback.call(instance);
 assert.equal(starts,1,'delayed callback must not reenable a disabled subtitle');
 let timers=0;
 scope.setTimeout=()=>{timers++;};
 const initial=src.indexOf('function startInitialSubtitleTrackTimeout('),audio=src.indexOf('function startInitialAudioTrackTimeout(',initial);
 vm.runInContext(src.slice(initial,audio),scope);
 scope.subtitleTrackIndexToSetOnPlaying=0;
 scope.startInitialSubtitleTrackTimeout(instance);await new Promise(resolve=>setImmediate(resolve));
 assert.equal(starts,2,'the first supported subtitle must start immediately');
 assert.equal(timers,0,'supported subtitles must not inherit the 400 ms native selection delay');
 console.log('PASS immediate first subtitle and manual choice survives delayed startup selection '+version);
}
(async()=>{for(const version of ['4.9.5.0','4.10.0.40'])await checkStartupSelection(version);})().catch(e=>{console.error(e);process.exitCode=1;});

// The capability marker is emitted only by the patched HTML video player,
// independently of browser brands and without mutating a shared profile object.
async function checkProfileMarker(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const at=src.indexOf('function strmBridgeHlsCapable('),end=src.indexOf('function strmBridgeCandidate(',at);
 assert.ok(at>=0&&end>at);
 for(const [capable,hostHls,chromecast] of [[true,true,false],[false,true,false],[true,false,false],[true,true,true]]) {
   const original={Name:'Browser profile',CodecProfiles:[{Codec:'h264'}]};
   const scope={window:capable?{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1}:{},HtmlVideoPlayer:function(){},
     _browser:{default:{chromecast}},_htmlmediahelper:{default:{enableHlsJsPlayer:(runtime,type)=>{assert.equal(runtime,null);assert.equal(type,'Video');return hostHls;}}}};
   scope.HtmlVideoPlayer.prototype.getDeviceProfile=async()=>original;
   vm.runInNewContext(src.slice(at,end),scope);
   const start=src.indexOf('function HtmlVideoPlayer('),stop=src.indexOf('function onPictureInPictureError(',start);
   scope.resetVideoRendererSize=()=>{};
   scope._basehtmlplayer={default:function(){this.getDeviceProfile=async()=>original;}};
   vm.runInNewContext(src.slice(start,stop),scope);
   const result=await new scope.HtmlVideoPlayer().getDeviceProfile();
   assert.equal(result.Name,capable&&hostHls&&!chromecast?'STRM Bridge Web subtitles v2':'Browser profile');
   assert.equal(original.Name,'Browser profile');
   assert.equal(result.CodecProfiles,original.CodecProfiles);
 }
 console.log('PASS Web capability negotiation marker and unsupported-engine guard '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkProfileMarker(v);})().catch(e=>{console.error(e);process.exitCode=1;});

// External ASS keeps the host's loader; only its observable HLS clock is bound.
async function checkExternalClockDispatch(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const start=src.indexOf('function renderWithSubtitlesOctopus('),end=src.indexOf('function renderWithSubtitlesOctopusNative(',start);
 assert.ok(start>=0&&end>start);
 let native=0,clocks=0,imports=0;const renderFlags=[];
 const scope={URL,document:{baseURI:'http://localhost/web/index.html'},Emby:{importModule:async()=>{imports++;return{bindExternalClock:()=>clocks++};}},
   renderWithSubtitlesOctopusNative:async(...args)=>{native++;renderFlags.push(args.at(-1));},_connectionmanager:{default:{getApiClient:()=>({})}}};
 vm.createContext(scope);vm.runInContext(src.slice(start,end),scope);
 const instance={_hlsPlayer:{},_strmBridgeEpoch:1,_currentPlayOptions:{url:'http://localhost/master.m3u8?SegmentContainer=ts'}};
 const track={IsExternal:true,Codec:'ass'},media={Container:'mkv',Protocol:'Http'};
 await scope.renderWithSubtitlesOctopus(instance,{},track,{},media);assert.equal(native,1);assert.equal(clocks,1);assert.equal(renderFlags[0],true);
 for(const [player,t,m] of [[instance,{...track,IsExternal:false},media],[{},track,media],[instance,track,{...media,Protocol:'File'}],[instance,{...track,Codec:'srt'},media]])
   await scope.renderWithSubtitlesOctopus(player,{},t,{},m);
 assert.equal(native,5);assert.equal(clocks,1);assert.equal(imports,1);assert.ok(renderFlags.slice(1).every(v=>v===undefined));
 let release;scope.Emby.importModule=()=>new Promise(r=>{release=r;});
 const delayed=scope.renderWithSubtitlesOctopus(instance,{},track,{},media);instance._strmBridgeEpoch++;
 release({bindExternalClock:()=>clocks++});await delayed;assert.equal(native,5);assert.equal(clocks,1);
 console.log('PASS external ASS host loading, clock-only dispatch, scope and stale-import exclusion '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkExternalClockDispatch(v);})().catch(e=>{console.error(e);process.exitCode=1;});

async function checkExternalLoaderEpoch(version) {
 const src=fs.readFileSync(require('node:path').join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 const begin=src.indexOf('function renderWithSubtitlesOctopusNative('),end=src.indexOf('function renderAssSsa(',begin);
 let release,constructed=0;
 const scope={window:{},_browser:{default:{}},fetch:()=>{},getTextTrackUrl:()=>'/fixture.ass',loadWebVTT:async()=>{},
   fetchSubtitleContent:async()=>'',getFallbackFontUrl:async()=>'/fixture.woff2',
   Emby:{importModule:()=>new Promise(resolve=>{release=()=>resolve(function(){constructed++;});})}};
 vm.createContext(scope);vm.runInContext(src.slice(begin,end),scope);
 const instance={_strmBridgeEpoch:1};
 const pending=scope.renderWithSubtitlesOctopusNative(instance,{},{},{},{});
 assert.equal(typeof pending?.then,'function','Both host loaders must expose renderer completion.');
 await new Promise(resolve=>setImmediate(resolve));instance._strmBridgeEpoch++;
 release();await pending;assert.equal(constructed,0);
 console.log('PASS stale native ASS content/font response cannot recreate a closed track '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkExternalLoaderEpoch(v);})().catch(e=>{console.error(e);process.exitCode=1;});

// Real host selection + playback-manager startup, with delayed HLS/metadata.
// No native extraction should start while the intended HLS player is loading.
async function checkLoadingSelection(version) {
 const path=require('node:path');
 const src=fs.readFileSync(path.join(process.argv[2] || '.local/subtitle-implementation','web-'+version+'.js'),'utf8');
 // Supply original host web roots to exercise the manager as well as the player.
 const root=process.argv[version.startsWith('4.9')?3:4];
 if (!root) return;
 const managerPath=path.join(root,'modules/common/playback/playbackmanager.js');
 assert.ok(fs.existsSync(managerPath),'Supply both original host web roots for the startup check.');
 const manager=fs.readFileSync(managerPath,'utf8'),starts=[],playerData={};let imports=0;
 const scope={window:{Worker:1,ReadableStream:1,AbortController:1,TextDecoder:1},console:{log(){}},
   isNativeLG:false,_browser:{default:{}},_htmlmediahelper:{default:{enableHlsJsPlayer:()=>true}},
   sortMediaStreamTextTracks:()=>0,getMediaStreamSubtitleTracks:s=>s.MediaStreams,
   removeCueEvents:()=>{},addCueEvents:()=>{},setCueAppearance:()=>{},enableChunkedResponse:()=>false,
   _connectionmanager:{default:{getApiClient:()=>({})}},
   Emby:{importModule:async()=>{imports++;return{render:async o=>{
     starts.push(o.track.Index);o.instance.strmBridgeSubtitle={dispose(){}};
   }};}},renderTracksEventsNative:()=>{throw Error('premature native subtitle fallback');},
   subtitleTrackIndexToSetOnPlaying:-1,initialSubtitleTrackTimeout:null,clearTimeout,setTimeout,
   HtmlVideoPlayer:function(){},getPlayerData:()=>playerData,_events:{default:{trigger(){}}}};
 vm.createContext(scope);
 const wrapper=src.indexOf('function strmBridgeHlsCapable('),native=src.indexOf('function renderTracksEventsNative(',wrapper);
 vm.runInContext(src.slice(wrapper,native),scope);
 const d=src.indexOf('function destroyCustomTrack(');vm.runInContext(src.slice(d,src.indexOf('function ',d+10)),scope);
 const c=src.indexOf('function setTrackForCustomDisplay('),t=src.indexOf('function startInitialSubtitleTrackTimeout(',c);
 vm.runInContext(src.slice(c,t),scope);
 const tracks=[2,3].map(Index=>({Index,Type:'Subtitle',Codec:'ass',IsExternal:false,DeliveryMethod:'External'}));
 const source={Container:'mkv',Protocol:'Http',MediaStreams:tracks,DefaultSubtitleStreamIndex:-1};
 const p={_mediaElement:{textTracks:[],readyState:0},customTrackIndex:-1,setSubtitleOffset(){},
   _currentPlayOptions:{url:'/fixture/master.m3u8',item:{Id:'fixture'},mediaSource:source}};
 scope.self=p;
 const setter=version.startsWith('4.9')?'self.setSubtitleStreamIndex=function(index){':'HtmlVideoPlayer.prototype.setSubtitleStreamIndex=function(index){';
 const i=src.indexOf(setter);vm.runInContext(src.slice(i,src.indexOf('},',i)+1)+';',scope);
 const select=version.startsWith('4.9')?p.setSubtitleStreamIndex:scope.HtmlVideoPlayer.prototype.setSubtitleStreamIndex;
 const marker='this.boundonInitialSubtitleTrackTimeout=',j=src.indexOf(marker)+marker.length,k=src.indexOf('}.bind(this)',j)+1;
 const startup=vm.runInContext('('+src.slice(j,k)+')',scope);
 select.call(p,2);select.call(p,3);startup.call(p);await new Promise(setImmediate);
 assert.equal(imports,0,'loading must not start either subtitle renderer');assert.equal(p.customTrackIndex,-1);
 select.call(p,-1);assert.equal(scope.subtitleTrackIndexToSetOnPlaying,-1,'off cancels a queued choice without an active renderer');
 p._hlsPlayer={};p._mediaElement.readyState=4;startup.call(p);await new Promise(setImmediate);
 assert.deepEqual(starts,[],'cancelled startup must remain off');
 p._mediaElement.readyState=0;select.call(p,2);select.call(p,3);startup.call(p);await new Promise(setImmediate);
 assert.equal(imports,0,'HLS creation alone does not imply metadata/job readiness');
 p._mediaElement.readyState=4;startup.call(p);await new Promise(setImmediate);
 assert.deepEqual(starts,[3],'apply only the latest queued track');
 scope.self.getPlayerState=()=>({PlayState:{}});
 const m=manager.indexOf('function onPlaybackStarted('),n=manager.indexOf('function onPlayQueueStartedFromSelfManagingPlayer(',m);
 assert.ok(m>=0&&n>m);vm.runInContext(manager.slice(m,n),scope);
 await scope.onPlaybackStarted(p,{command:'fixture'},{mediaSource:source,item:{}},source,false,false);
 assert.equal(playerData.subtitleStreamIndex,3,'startup must not reset the menu to the original off default');
 p._started=true;p._mediaElement.readyState=0;
 select.call(p,2);await new Promise(setImmediate);assert.deepEqual(starts,[3,2],'metadata loss after playback must not queue behind a startup callback that has already fired');
 select.call(p,-1);await scope.onPlaybackStarted(p,{command:'fixture'},{mediaSource:source,item:{}},source,false,false);
 assert.equal(playerData.subtitleStreamIndex,-1,'explicit off is retained by the manager');
 // Cover every three-choice combination, including leaving and reentering
 // bridge rendering. Native VTT tracks execute the real host selection path.
 const external=[4,5].map(Index=>({Index,Type:'Subtitle',Codec:'subrip',DeliveryFormat:'vtt',
   IsExternal:true,DeliveryMethod:'External',IsTextSubtitleStream:true}));
 source.MediaStreams.push(...external);
 p._mediaElement.textTracks=external.map(t=>({id:'textTrack'+t.Index,label:'external',mode:'disabled'}));
 scope.destroyCustomTrack(p,p._mediaElement);
 p._started=false;p._hlsPlayer=null;p._mediaElement.readyState=0;
 select.call(p,2);select.call(p,4);select.call(p,-1);
 assert.equal(source.DefaultSubtitleStreamIndex,-1,'embedded -> external -> off must not retain the external default');
 startup.call(p);
 assert.ok(p._mediaElement.textTracks.every(t=>t.mode==='disabled'),'startup must not reenable external subtitles after off');
 const choices=[-1,2,3,4,5];let combinations=0;
 for(const a of choices)for(const b of choices)for(const last of choices){
   scope.destroyCustomTrack(p,p._mediaElement);
   p._started=false;p._hlsPlayer=null;p._mediaElement.readyState=0;
   source.DefaultSubtitleStreamIndex=scope.subtitleTrackIndexToSetOnPlaying=-1;
   for(const index of [a,b,last])select.call(p,index);
   assert.equal(source.DefaultSubtitleStreamIndex,last,'runtime default must retain the last manual choice');
   p._hlsPlayer={};p._mediaElement.readyState=4;p._started=true;
   startup.call(p);await new Promise(setImmediate);
   await scope.onPlaybackStarted(p,{command:'fixture'},{mediaSource:source,item:{}},source,false,false);
   assert.equal(playerData.subtitleStreamIndex,last,'startup menu must agree with the last manual choice');
   assert.equal(p.customTrackIndex,last===2||last===3?last:-1);
   for(const t of p._mediaElement.textTracks)
     assert.equal(t.mode,t.id==='textTrack'+last?'hidden':'disabled','startup reenabled an obsolete native track');
   combinations++;
 }
 // The reused player must not keep synchronization ownership for another source.
 scope.destroyCustomTrack(p,p._mediaElement);
 const previousDefault=source.DefaultSubtitleStreamIndex;
 const nativeSource={Container:'mkv',Protocol:'Http',MediaStreams:external,DefaultSubtitleStreamIndex:-1};
 p._currentPlayOptions={mediaSource:nativeSource};scope.subtitleTrackIndexToSetOnPlaying=-1;
 select.call(p,4);
 assert.equal(nativeSource.DefaultSubtitleStreamIndex,-1,'a source without bridge tracks retains native initialization');
 assert.equal(scope.subtitleTrackIndexToSetOnPlaying,-1);
 assert.equal(source.DefaultSubtitleStreamIndex,previousDefault,'the previous play source must not be modified');
 p._currentPlayOptions=null;scope.strmBridgeSelect(p,-1);
 assert.equal(scope.subtitleTrackIndexToSetOnPlaying,-1,'a stopped player must not retain selection ownership');
 console.log('PASS '+combinations+' mixed embedded/external/off sequences and next-source isolation '+version);
 console.log('PASS queued subtitle switching/off, HLS metadata readiness and actual playback-manager menu state '+version);
}
(async()=>{for(const v of ['4.9.5.0','4.10.0.40'])await checkLoadingSelection(v);})().catch(e=>{console.error(e);process.exitCode=1;});
