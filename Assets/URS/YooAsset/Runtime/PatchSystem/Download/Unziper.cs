﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using URS;

namespace YooAsset
{
	public class Unziper
	{
		private enum ESteps
		{
			None,
			CreateDownload,
			CheckDownload,
			TryAgain,
			Succeed,
			Failed,
		}

		private readonly UnzipEntry _unzipEntry;
		private UnityWebRequest _webRequest;
		private UnityWebRequestAsyncOperation _operationHandle;

		private ESteps _steps = ESteps.None;
		private string _lastError = string.Empty;

		private int _timeout;
		private int _failedTryAgain;
		private int _requestCount;
		private string _requestURL;

		// 重置变量
		private bool _isAbort = false;
		private ulong _latestDownloadBytes;
		private float _latestDownloadRealtime;
		private float _tryAgainTimer;

		/// <summary>
		/// 下载进度（0-100f）
		/// </summary>
		public float DownloadProgress { private set; get; }

		/// <summary>
		/// 已经下载的总字节数
		/// </summary>
		public ulong DownloadedBytes { private set; get; }


		internal Unziper(UnzipEntry result)
		{
			_unzipEntry = result;
		}
		internal void SendRequest(int failedTryAgain, int timeout)
		{
			if (string.IsNullOrEmpty(_unzipEntry.HardiskSavePath))
				throw new ArgumentNullException();

			if (_steps == ESteps.None)
			{
				_failedTryAgain = failedTryAgain;
				_timeout = timeout;
				_steps = ESteps.CreateDownload;
			}
		}
		internal void Update()
		{
			if (_steps == ESteps.None)
				return;
			if (_steps == ESteps.Failed || _steps == ESteps.Succeed)
				return;

			// 创建下载器
			if (_steps == ESteps.CreateDownload)
			{
				// 重置变量
				DownloadProgress = 0f;
				DownloadedBytes = 0;
				_isAbort = false;
				_latestDownloadBytes = 0;
				_latestDownloadRealtime = Time.realtimeSinceStartup;
				_tryAgainTimer = 0f;

				_requestCount++;
				_requestURL = GetRequestURL();
				_webRequest = new UnityWebRequest(_requestURL, UnityWebRequest.kHttpVerbGET);
				DownloadHandlerFile handler = new DownloadHandlerFile(_unzipEntry.HardiskSavePath);
				handler.removeFileOnAbort = true;
				_webRequest.downloadHandler = handler;
				_webRequest.disposeDownloadHandlerOnDispose = true;
				_operationHandle = _webRequest.SendWebRequest();
				_steps = ESteps.CheckDownload;
			}

			// 检测下载结果
			if (_steps == ESteps.CheckDownload)
			{
				DownloadProgress = _webRequest.downloadProgress * 100f;
				DownloadedBytes = _webRequest.downloadedBytes;
				if (_operationHandle.isDone == false)
				{
					CheckTimeout();
					return;
				}

				// 检查网络错误
				bool isError = false;
#if UNITY_2020_3_OR_NEWER
				if (_webRequest.result != UnityWebRequest.Result.Success)
				{
					isError = true;
					_lastError = _webRequest.error;
				}
#else
				if (_webRequest.isNetworkError || _webRequest.isHttpError)
				{
					isError = true;
					_lastError = _webRequest.error;
				}
#endif

				// 检查文件完整性
				if (isError == false)
				{
					// 注意：如果文件验证失败需要删除文件
					if (URSFileSystem.DownloadFolderCheckContentIntegrity(_unzipEntry.StreamFileMeta, _unzipEntry.HardiskSavePath) == false)
					{
						isError = true;
						_lastError = $"Verification failed";			
						if (File.Exists(_unzipEntry.HardiskSavePath))
							File.Delete(_unzipEntry.HardiskSavePath);
					}
				}

				if (isError)
				{
					ReportError();
					if (_failedTryAgain > 0)
					{
						_steps = ESteps.TryAgain;
					}
					else 
					{
                        _steps = ESteps.Failed;
                        OnFinish();
                    }
						
				}
				else
				{
					
                    //Logger.Log($"unzip 下载完毕{_unzipEntry.HardiskSavePath} ");
                    URSFileSystem.DownloadFolderRegisterVerifyFile(_unzipEntry.StreamFileMeta);
                    _steps = ESteps.Succeed;
                    OnFinish();
                }

				// 释放下载器
				DisposeWebRequest();
			}

			// 重新尝试下载
			if (_steps == ESteps.TryAgain)
			{
				_tryAgainTimer += Time.unscaledDeltaTime;
				if (_tryAgainTimer > 0.5f)
				{
					_failedTryAgain--;
					_steps = ESteps.CreateDownload;
					Logger.Warning($"Try again download : {_requestURL}");
				}
			}
		}
		internal void SetDone()
		{
			_steps = ESteps.Succeed;
			OnFinish();

        }
		private void OnFinish() 
		{
			var callback = _unzipEntry.OnFinish;
			if (callback != null)
			{
                callback(this);
            }
        }

		private string GetRequestURL()
		{
			return _unzipEntry.HardiskSourcePath;

        }
		private void CheckTimeout()
		{
			// 注意：在连续时间段内无新增下载数据及判定为超时
			if (_isAbort == false)
			{
				if (_latestDownloadBytes != DownloadedBytes)
				{
					_latestDownloadBytes = DownloadedBytes;
					_latestDownloadRealtime = Time.realtimeSinceStartup;
				}

				float offset = Time.realtimeSinceStartup - _latestDownloadRealtime;
				if (offset > _timeout)
				{
					Logger.Warning($"Web file request timeout : {_requestURL}");
					_webRequest.Abort();
					_isAbort = true;
				}
			}
		}
		private void DisposeWebRequest()
		{
			if (_webRequest != null)
			{
				_webRequest.Dispose();
				_webRequest = null;
				_operationHandle = null;
			}
		}

		/// <summary>
		/// 获取资源包信息
		/// </summary>
		public UnzipEntry GetBundleInfo()
		{
			return _unzipEntry;
		}

		/// <summary>
		/// 检测下载器是否已经完成（无论成功或失败）
		/// </summary>
		public bool IsDone()
		{
			return _steps == ESteps.Succeed || _steps == ESteps.Failed;
		}

		/// <summary>
		/// 下载过程是否发生错误
		/// </summary>
		/// <returns></returns>
		public bool HasError()
		{
			return _steps == ESteps.Failed;
		}

		/// <summary>
		/// 报告错误信息
		/// </summary>
		public void ReportError()
		{
			Logger.Error($"Failed to download : {_requestURL} Error : {_lastError}");
		}
	}
}