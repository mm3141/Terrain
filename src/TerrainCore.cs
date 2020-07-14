using System;
using System.Runtime.InteropServices;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

using Map=ExileCore.PoEMemory.Elements.Map;

namespace Terrain
{
	internal class TerrainCore : BaseSettingsPlugin<TerrainSettings>
	{
		private CachedValue<float> _diag;
		private IngameUIElements _ingameStateIngameUi;
		private float _k;
		private bool _largeMap;
		private CachedValue<RectangleF> _mapRect;
		private int _numRows, _numCols;
		private float _scale;
		private Vector2 _screenCenterCache;
		private int[] _bitmap;
		private GCHandle _bitmapHandle;

		public TerrainCore()
		{
			Name = "Terrain";
		}

		private RectangleF MapRect => _mapRect?.Value ??
		                              (_mapRect = new TimeCache<RectangleF>(() => MapWindow.GetClientRect(), 100))
		                              .Value;

		private Map MapWindow => GameController.Game.IngameState.IngameUi.Map;
		private Camera Camera => GameController.Game.IngameState.Camera;

		private float Diag =>
			_diag?.Value ?? (_diag = new TimeCache<float>(() =>
			{
				if (_ingameStateIngameUi.Map.SmallMiniMap.IsVisibleLocal)
				{
					var mapRect = _ingameStateIngameUi.Map.SmallMiniMap.GetClientRect();
					return (float) (Math.Sqrt(mapRect.Width * mapRect.Width + mapRect.Height * mapRect.Height) / 2f);
				}

				return (float) Math.Sqrt(Camera.Width * Camera.Width + Camera.Height * Camera.Height);
			}, 100)).Value;

		private Vector2 ScreenCenter =>
			new Vector2(MapRect.Width / 2, MapRect.Height / 2 - 20) + new Vector2(MapRect.X, MapRect.Y) +
			new Vector2(MapWindow.LargeMapShiftX, MapWindow.LargeMapShiftY);

		public override void AreaChange(AreaInstance area)
		{
			if (!Settings.Enable)
				return;
			var terrain = GameController.IngameState.Data.Terrain;
			var terrainBytes = GameController.Memory.ReadBytes(terrain.LayerMelee.First, terrain.LayerMelee.Size);
			
			_numCols = (int) terrain.NumCols * 23;
			_numRows = (int) terrain.NumRows * 23;
			if ((_numCols & 1) > 0)
				_numCols++;
			
			_bitmap = new int[_numCols * _numRows];
			int k = 0;
			int dataIndex = 0;
			var color = Settings.TerrainColor.Value.ToRgba();
			for (int i = 0; i < _numRows; i++)
			{
				for (int j = 0; j < _numCols; j += 2)
				{
					var b = terrainBytes[dataIndex + (j >> 1)];
					_bitmap[k++] = (b >> 4) > 0 ? color : 0;
					_bitmap[k++] = (b & 0xf) > 0 ? color : 0;
				}
				dataIndex += terrain.BytesPerRow;
			}

			if (_bitmapHandle.IsAllocated)
				_bitmapHandle.Free();
			_bitmapHandle = GCHandle.Alloc(_bitmap, GCHandleType.Pinned);

			var texture = new Texture2D(Graphics.LowLevel.D11Device, new Texture2DDescription
			{
				ArraySize = 1,
				Height = _numRows,
				Width = _numCols,
				Format = Format.R8G8B8A8_UNorm,
				BindFlags = BindFlags.ShaderResource,
				Usage = ResourceUsage.Default,
				MipLevels = 1,
				CpuAccessFlags = CpuAccessFlags.Write,
				SampleDescription = new SampleDescription(1, 0)
			}, new[] {new DataBox(_bitmapHandle.AddrOfPinnedObject(), sizeof(int)*_numCols, 0)});
			var srv = new ShaderResourceView(Graphics.LowLevel.D11Device, texture);
			Graphics.LowLevel.AddOrUpdateTexture("terrain", srv);
		}

		public override Job Tick()
		{
			TickLogic();
			return null;
		}

		private void TickLogic()
		{
			try
			{
				_ingameStateIngameUi = GameController.Game.IngameState.IngameUi;

				if (_ingameStateIngameUi.Map.SmallMiniMap.IsVisibleLocal)
				{
					var mapRect = _ingameStateIngameUi.Map.SmallMiniMap.GetClientRectCache;
					_screenCenterCache = new Vector2(mapRect.X + mapRect.Width / 2, mapRect.Y + mapRect.Height / 2);
					_largeMap = false;
				}
				else if (_ingameStateIngameUi.Map.LargeMap.IsVisibleLocal)
				{
					_screenCenterCache = ScreenCenter;
					_largeMap = true;
				}

				_k = Camera.Width < 1024f ? 1120f : 1024f;
				_scale = _k / Camera.Height * Camera.Width * 3f / 4f / MapWindow.LargeMapZoom;
			}
			catch (Exception e)
			{
				DebugWindow.LogError($"Terrain.TickLogic: {e.Message}");
			}
		}

		public override void Render()
		{
			if (!Settings.Enable || !_largeMap || _ingameStateIngameUi.AtlasPanel.IsVisibleLocal ||
			    _ingameStateIngameUi.DelveWindow.IsVisibleLocal ||
			    _ingameStateIngameUi.TreePanel.IsVisibleLocal)
				return;

			var playerPos = GameController.Player.GetComponent<Positioned>().GridPos;
			var posZ = GameController.Player.GetComponent<Render>().Pos.Z;
			var mapWindowLargeMapZoom = MapWindow.LargeMapZoom;

			Vector2 Transform(Vector2 p) => _screenCenterCache + DeltaInWorldToMinimapDelta(p - playerPos, Diag, _scale, -posZ / (9f / mapWindowLargeMapZoom));

			Graphics.DrawTexture(Graphics.LowLevel.GetTexture("terrain").NativePointer,
				Transform(new Vector2(0, 0)),
				Transform(new Vector2(_numCols, 0)),
				Transform(new Vector2(_numCols, _numRows)),
				Transform(new Vector2(0, _numRows))
			);
		}

		public Vector2 DeltaInWorldToMinimapDelta(Vector2 delta, double diag, float scale, float deltaZ = 0)
		{
			var CAMERA_ANGLE = 38 * MathUtil.Pi / 180;

			// Values according to 40 degree rotation of cartesian coordiantes, still doesn't seem right but closer
			var cos = (float) (diag * Math.Cos(CAMERA_ANGLE) / scale);
			var sin = (float) (diag * Math.Sin(CAMERA_ANGLE) /
			                   scale); // possible to use cos so angle = nearly 45 degrees

			// 2D rotation formulas not correct, but it's what appears to work?
			return new Vector2((delta.X - delta.Y) * cos, deltaZ - (delta.X + delta.Y) * sin);
		}
	}
}