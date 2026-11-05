using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using KeepAlive.Core;
using ResourcesWork;

namespace KeepAlive.Framework.Audio
{
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [Header("音量设置 (0-1)")]
        [Range(0, 1)] public float BGMVolume = 0.8f;
        [Range(0, 1)] public float SFXVolume = 1.0f;

        private AudioSource _bgmSource;
        private AudioSource _uiSFXSource;
        private readonly Queue<AudioSource> _sfxPool = new();
        private readonly List<AudioSource> _activeSfxList = new();
        private readonly Dictionary<string, IResourceHandle> _audioHandles = new();

        private Coroutine _bgmFadeCoroutine;
        /// <summary>
        /// ★ 新增：用于解决快速切 BGM 竞态
        /// </summary>
        private int _bgmRequestId = 0;

        private string _currentBgmKey;   // ★ 新增：记录当前播放的 key（不是 clip.name）

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            InitAudioSources();
        }

        private void InitAudioSources()
        {
            _bgmSource = gameObject.AddComponent<AudioSource>();
            _bgmSource.loop = true;
            _bgmSource.playOnAwake = false;
            _bgmSource.volume = BGMVolume;

            _uiSFXSource = gameObject.AddComponent<AudioSource>();
            _uiSFXSource.playOnAwake = false;
            _uiSFXSource.volume = SFXVolume;

            // 预生成 10 个 3D 音效源
            for (int i = 0; i < 10; i++)
            {
                _sfxPool.Enqueue(CreateNewSource());
            }
        }

        /// <summary>
        /// ★ 拆分：纯粹的创建逻辑，不碰队列
        /// </summary>
        /// <returns></returns>
        private AudioSource CreateNewSource()
        {
            var go = new GameObject("SFX_Source");
            go.transform.SetParent(transform);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1.0f;
            source.volume = SFXVolume;
            return source;
        }

        /// <summary>
        /// ★ 拆分：获取可用源（池里有就取，没有就建）
        /// </summary>
        /// <returns></returns>
        private AudioSource GetAvailableSource()
        {
            if (_sfxPool.Count > 0)
            {
                return _sfxPool.Dequeue();
            }

            KeepAlive.Core.Logger.Warning("Audio", "3D音效池耗尽，自动扩容一个");
            return CreateNewSource();
        }

        // ── BGM 控制 ──────────────────────────────────────────────
        /// <summary>
        /// 开启BGM
        /// </summary>
        /// <param name="key"></param>
        /// <param name="fadeTime"></param>
        public async void PlayBGM(string key, float fadeTime = 1.0f)
        {
            if (_bgmSource.clip != null && _currentBgmKey == key) return;

            // ★ 1. 递增请求版本号
            int requestId = ++_bgmRequestId;

            var handle = await LoadAudioClipAsync(key);

            // ★ 2. 校验版本号：如果在这个 await 期间，又调用了新的 PlayBGM，则本次作废
            if (requestId != _bgmRequestId) return;

            if (handle == null || handle.Asset == null) return;

            if (_bgmFadeCoroutine != null) StopCoroutine(_bgmFadeCoroutine);
            _bgmFadeCoroutine = StartCoroutine(FadeBGM(handle.Asset, fadeTime, key));
        }
        /// <summary>
        /// 关闭BGM
        /// </summary>
        /// <param name="newClip"></param>
        /// <param name="fadeTime"></param>
        /// <returns></returns>
        private IEnumerator FadeBGM(AudioClip newClip, float fadeTime, string newKey)
        {
            if (_bgmSource.isPlaying)
            {
                float startVol = _bgmSource.volume;
                for (float t = 0; t < fadeTime; t += Time.unscaledDeltaTime)
                {
                    _bgmSource.volume = Mathf.Lerp(startVol, 0, t / fadeTime);
                    yield return null;
                }
                _bgmSource.volume = 0;
                _bgmSource.Stop();
            }

            _bgmSource.clip = newClip;
            _currentBgmKey = newKey;   // ★ 在换曲时更新
            _bgmSource.Play();

            for (float t = 0; t < fadeTime; t += Time.unscaledDeltaTime)
            {
                _bgmSource.volume = Mathf.Lerp(0, BGMVolume, t / fadeTime);
                yield return null;
            }
            _bgmSource.volume = BGMVolume;
        }

        // ── SFX 控制 ──────────────────────────────────────────────
        /// <summary>
        /// 开启UI音效
        /// </summary>
        /// <param name="key"></param>
        public async void PlayUISFX(string key)
        {
            try
            {
                var handle = await LoadAudioClipAsync(key);
                if (handle != null && handle.Asset != null)
                {
                    _uiSFXSource.PlayOneShot(handle.Asset, SFXVolume);
                }
            }
            catch (Exception e)
            {
                KeepAlive.Core.Logger.Exception("Audio", e, $"播放 UI 音效异常: {key}");
            }
        }
        /// <summary>
        /// 开启3D音效
        /// </summary>
        /// <param name="key"></param>
        /// <param name="position"></param>
        public async void Play3DSFX(string key, Vector3 position)
        {
            try
            {
                var handle = await LoadAudioClipAsync(key);
                if (handle == null || handle.Asset == null) return;

                var source = GetAvailableSource();
                source.transform.position = position;
                source.clip = handle.Asset;
                source.Play();

                _activeSfxList.Add(source);
                StartCoroutine(RecycleSFX(source, handle.Asset.length));
            }
            catch (Exception e)
            {
                KeepAlive.Core.Logger.Exception("Audio", e, $"播放 3D 音效异常: {key}");
            }
        }
        /// <summary>
        /// 清理音效
        /// </summary>
        /// <param name="source"></param>
        /// <param name="clipLength"></param>
        /// <returns></returns>
        private IEnumerator RecycleSFX(AudioSource source, float clipLength)
        {
            yield return new WaitForSeconds(clipLength);

            // ★ 如果在等待期间被 ClearAudio 清理了，就直接放弃回收
            if (!_activeSfxList.Contains(source)) yield break;

            source.Stop();
            source.clip = null;
            _activeSfxList.Remove(source);
            _sfxPool.Enqueue(source);
        }

        // ── 辅助加载与释放 ──────────────────────────────────────────
        /// <summary>
        /// 加载音频并缓存句柄。缓存会持续增长直到 ClearAudio，
        /// 因为所有播放过的音频都常驻内存（避免重复加载）。
        /// 如果音效种类很多，可能需要在业务层做"按需清理"。
        /// </summary>
        private async Task<ResourceHandle<AudioClip>> LoadAudioClipAsync(string key)
        {
            var newHandle = await ResourceManager.LoadAssetAsync<AudioClip>(key);

            if (newHandle == null || newHandle.Asset == null)
            {
                newHandle?.Dispose(); // 防御性释放
                KeepAlive.Core.Logger.Error("Audio", $"加载音频失败: {key}");
                return null;
            }

            if (_audioHandles.TryGetValue(key, out var existingHandle))
            {
                // ★ 核心修复：如果已经有旧句柄了，释放本次新请求的引用，返回旧句柄
                newHandle.Dispose();
                return existingHandle as ResourceHandle<AudioClip>;
            }

            // 首次加载，记录句柄
            _audioHandles[key] = newHandle;
            return newHandle;
        }

        // ── 音量控制 ──────────────────────────────────────────────
        /// <summary>
        /// 控制BGM声音大小
        /// </summary>
        /// <param name="volume"></param>
        public void SetBGMVolume(float volume)
        {
            BGMVolume = Mathf.Clamp01(volume);
            if (_bgmSource != null) _bgmSource.volume = BGMVolume;
        }
        /// <summary>
        /// 控制音效声音大小
        /// </summary>
        /// <param name="volume"></param>
        public void SetSFXVolume(float volume)
        {
            SFXVolume = Mathf.Clamp01(volume);
            foreach (var source in _activeSfxList) source.volume = SFXVolume;
            foreach (var source in _sfxPool) source.volume = SFXVolume;
        }

        // ── 清理 ──────────────────────────────────────────────

        /// <summary>
        /// 场景切换时清理并释放资源。
        /// ★ 必须由 SceneLoader 切场景时显式调用！
        /// </summary>
        public void ClearAudio()
        {
            // 1. 清 BGM
            _bgmSource.Stop();
            _bgmSource.clip = null;
            _currentBgmKey = null;   // ★ 重置
            if (_bgmFadeCoroutine != null) StopCoroutine(_bgmFadeCoroutine);
            _bgmRequestId++; // 阻断还在加载中的 BGM 请求

            // ★ 新增：停止 UI 音效（PlayOneShot 队列）
            _uiSFXSource.Stop();

            // 2. 清 3D 音效（★ 彻底清理：停下、清空列表）
            foreach (var source in _activeSfxList)
            {
                source.Stop();
                source.clip = null;
                _sfxPool.Enqueue(source); // 顺便还回池子
            }
            _activeSfxList.Clear();

            // 3. 释放所有音频资源句柄
            foreach (var handle in _audioHandles.Values)
            {
                handle.Dispose();
            }
            _audioHandles.Clear();

            KeepAlive.Core.Logger.Info("Audio", "音频资源与播放源已彻底清理");
        }
    }
}