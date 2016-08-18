﻿using System;
using SharpDX;
using System.Collections.Generic;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    public class ShapesApp : D3DApp
    {
        private readonly List<FrameResource> _frameResources = new List<FrameResource>(NumFrameResources);
        private readonly List<AutoResetEvent> _fenceEvents = new List<AutoResetEvent>(NumFrameResources);
        private int _currFrameResourceIndex;

        private RootSignature _rootSignature;
        private DescriptorHeap _cbvHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private readonly Dictionary<string, MeshGeometry> _geometries = new Dictionary<string, MeshGeometry>();
        private readonly Dictionary<string, ShaderBytecode> _shaders = new Dictionary<string, ShaderBytecode>();
        private readonly Dictionary<string, PipelineState> _psos = new Dictionary<string, PipelineState>();

        private InputLayoutDescription _inputLayout;

        // List of all the render items.
        private readonly List<RenderItem> _allRitems = new List<RenderItem>();

        // Render items divided by PSO.
        private readonly List<RenderItem> _opaqueRitems = new List<RenderItem>();

        private PassConstants _mainPassCB;

        private int _passCbvOffset;

        private bool _isWireframe = true;

        private Vector3 _eyePos;
        private Matrix _proj = Matrix.Identity;
        private Matrix _view = Matrix.Identity;

        private float _theta = 1.5f * MathUtil.Pi;
        private float _phi = 0.2f * MathUtil.Pi;
        private float _radius = 15.0f;

        private Point _lastMousePos;

        public ShapesApp(IntPtr hInstance) : base(hInstance)
        {
            MainWindowCaption = "Shapes";
        }

        private FrameResource CurrFrameResource => _frameResources[_currFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => _fenceEvents[_currFrameResourceIndex];

        public override void Initialize()
        {
            base.Initialize();

            // Reset the command list to prep for initialization commands.
            CommandList.Reset(DirectCmdListAlloc, null);

            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildShapeGeometry();
            BuildRenderItems();
            BuildFrameResources();
            BuildDescriptorHeaps();
            BuildConstantBufferViews();
            BuildPSOs();

            // Execute the initialization commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until initialization is complete.
            FlushCommandQueue();
        }

        protected override void OnResize()
        {
            base.OnResize();

            // The window resized, so update the aspect ratio and recompute the projection matrix.
            _proj = Matrix.PerspectiveFovLH(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        protected override void Update(GameTimer gt)
        {
            UpdateCamera();

            // Cycle through the circular frame resource array.
            _currFrameResourceIndex = (_currFrameResourceIndex + 1) % NumFrameResources;

            // Has the GPU finished processing the commands of the current frame resource?
            // If not, wait until the GPU has completed commands up to this fence point.
            if (CurrFrameResource.Fence != 0 && Fence.CompletedValue < CurrFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrFrameResource.Fence, CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            UpdateObjectCBs();
            UpdateMainPassCB(gt);
        }

        protected override void Draw(GameTimer gt)
        {
            CommandAllocator cmdListAlloc = CurrFrameResource.CmdListAlloc;

            // Reuse the memory associated with command recording.
            // We can only reset when the associated command lists have finished execution on the GPU.
            cmdListAlloc.Reset();

            // A command list can be reset after it has been added to the command queue via ExecuteCommandList.
            // Reusing the command list reuses memory.
            CommandList.Reset(cmdListAlloc, _isWireframe ? _psos["opaque_wireframe"] : _psos["opaque"]);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

            // Clear the back buffer and depth buffer.
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);

            // Specify the buffers we are going to render to.            
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);

            CommandList.SetGraphicsRootSignature(_rootSignature);

            int passCbvIndex = _passCbvOffset + _currFrameResourceIndex;
            GpuDescriptorHandle passCbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
            passCbvHandle += passCbvIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(1, passCbvHandle);

            DrawRenderItems(CommandList, _opaqueRitems);

            // Indicate a state transition on the resource usage.
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

            // Done recording commands.
            CommandList.Close();

            // Add the command list to the queue for execution.
            CommandQueue.ExecuteCommandList(CommandList);

            // Present the buffer to the screen. Presenting will automatically swap the back and front buffers.
            SwapChain.Present(0, PresentFlags.None);

            // Advance the fence value to mark commands up to this fence point.
            CurrFrameResource.Fence = ++CurrentFence;

            // Add an instruction to the command queue to set a new fence point. 
            // Because we are on the GPU timeline, the new fence point won't be 
            // set until the GPU finishes processing all the commands prior to this Signal().
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            _lastMousePos = location;            
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.                
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - _lastMousePos.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - _lastMousePos.Y));

                // Update angles based on input to orbit camera around box.
                _theta += dx;
                _phi += dy;

                // Restrict the angle mPhi.
                _phi = MathUtil.Clamp(_phi, 0.1f, MathUtil.Pi - 0.1f);
            }
            else if ((button & MouseButtons.Right) != 0)
            {
                // Make each pixel correspond to a quarter of a degree.                
                float dx = 0.05f * (location.X - _lastMousePos.X);
                float dy = 0.05f * (location.Y - _lastMousePos.Y);

                // Update the camera radius based on input.
                _radius += dx - dy;

                // Restrict the radius.
                _radius = MathUtil.Clamp(_radius, 5.0f, 150.0f);
            }

            _lastMousePos = location;
        }

        protected override void OnKeyDown(Keys keyCode)
        {
            if (keyCode == Keys.D1)
                _isWireframe = false;
        }

        protected override void OnKeyUp(Keys keyCode)
        {
            base.OnKeyUp(keyCode);
            if (keyCode == Keys.D1)
                _isWireframe = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rootSignature?.Dispose();
                _cbvHeap?.Dispose();
                foreach (FrameResource frameResource in _frameResources) frameResource.Dispose();                
                foreach (MeshGeometry geometry in _geometries.Values) geometry.Dispose();
                foreach (PipelineState pso in _psos.Values) pso.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UpdateCamera()
        {
            // Convert Spherical to Cartesian coordinates.
            _eyePos.X = _radius * MathHelper.Sinf(_phi) * MathHelper.Cosf(_theta);
            _eyePos.Z = _radius * MathHelper.Sinf(_phi) * MathHelper.Sinf(_theta);
            _eyePos.Y = _radius * MathHelper.Cosf(_phi);

            // Build the view matrix.
            _view = Matrix.LookAtLH(_eyePos, Vector3.Zero, Vector3.Up);
        }

        private void UpdateObjectCBs()
        {
            foreach (RenderItem e in _allRitems)
            {
                // Only update the cbuffer data if the constants have changed.  
                // This needs to be tracked per frame resource. 
                if (e.NumFramesDirty > 0)
                {
                    var objConstants = new ObjectConstants { World = Matrix.Transpose(e.World) };
                    CurrFrameResource.ObjectCB.CopyData(e.ObjCBIndex, ref objConstants);

                    // Next FrameResource need to be updated too.
                    e.NumFramesDirty--;
                }
            }
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix viewProj = _view * _proj;
            Matrix invView = Matrix.Invert(_view);
            Matrix invProj = Matrix.Invert(_proj);
            Matrix invViewProj = Matrix.Invert(viewProj);

            _mainPassCB.View = Matrix.Transpose(_view);
            _mainPassCB.InvView = Matrix.Transpose(invView);
            _mainPassCB.Proj = Matrix.Transpose(_proj);
            _mainPassCB.InvProj = Matrix.Transpose(invProj);
            _mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            _mainPassCB.InvViewProj = Matrix.Transpose(invViewProj);
            _mainPassCB.EyePosW = _eyePos;
            _mainPassCB.RenderTargetSize = new Vector2(ClientWidth, ClientHeight);
            _mainPassCB.InvRenderTargetSize = 1.0f / _mainPassCB.RenderTargetSize;
            _mainPassCB.NearZ = 1.0f;
            _mainPassCB.FarZ = 1000.0f;
            _mainPassCB.TotalTime = gt.TotalTime;
            _mainPassCB.DeltaTime = gt.DeltaTime;

            CurrFrameResource.PassCB.CopyData(0, ref _mainPassCB);
        }

        private void BuildDescriptorHeaps()
        {
            int objCount = _opaqueRitems.Count;

            // Need a CBV descriptor for each object for each frame resource,
            // +1 for the perPass CBV for each frame resource.
            int numDescriptors = (objCount + 1) * NumFrameResources;

            // Save an offset to the start of the pass CBVs.  These are the last 3 descriptors.
            _passCbvOffset = objCount * NumFrameResources;

            var cbvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = numDescriptors,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            };
            _cbvHeap = Device.CreateDescriptorHeap(cbvHeapDesc);
            _descriptorHeaps = new[] { _cbvHeap };
        }

        private void BuildConstantBufferViews()
        {
            int objCBByteSize = D3DUtil.CalcConstantBufferByteSize<ObjectConstants>();

            int objCount = _opaqueRitems.Count;

            // Need a CBV descriptor for each object for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource objectCB = _frameResources[frameIndex].ObjectCB.Resource;
                for (int i = 0; i < objCount; i++)
                {
                    long cbAddress = objectCB.GPUVirtualAddress;

                    // Offset to the ith object constant buffer in the buffer.
                    cbAddress += i * objCBByteSize;

                    // Offset to the object cbv in the descriptor heap.
                    int heapIndex = frameIndex * objCount + i;
                    CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                    handle += heapIndex * CbvSrvUavDescriptorSize;

                    var cbvDesc = new ConstantBufferViewDescription
                    {
                        BufferLocation = cbAddress,
                        SizeInBytes = objCBByteSize
                    };

                    Device.CreateConstantBufferView(cbvDesc, handle);
                }
            }

            int passCBByteSize = D3DUtil.CalcConstantBufferByteSize<PassConstants>();

            // Last three descriptors are the pass CBVs for each frame resource.
            for (int frameIndex = 0; frameIndex < NumFrameResources; frameIndex++)
            {
                Resource passCB = _frameResources[frameIndex].PassCB.Resource;
                long cbAddress = passCB.GPUVirtualAddress;

                // Offset to the pass cbv in the descriptor heap.
                int heapIndex = _passCbvOffset + frameIndex;
                CpuDescriptorHandle handle = _cbvHeap.CPUDescriptorHandleForHeapStart;
                handle += heapIndex * CbvSrvUavDescriptorSize;

                var cbvDesc = new ConstantBufferViewDescription
                {
                    BufferLocation = cbAddress,
                    SizeInBytes = passCBByteSize
                };

                Device.CreateConstantBufferView(cbvDesc, handle);
            }
        }

        private void BuildRootSignature()
        {
            var cbvTable0 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0);
            var cbvTable1 = new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 1);

            // Root parameter can be a table, root descriptor or root constants.
            var slotRootParameters = new[]
            {
                new RootParameter(ShaderVisibility.Vertex, cbvTable0),
                new RootParameter(ShaderVisibility.Vertex, cbvTable1)
            };

            // A root signature is an array of root parameters.
            var rootSigDesc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                slotRootParameters);

            // Create a root signature with a single slot which points to a descriptor range consisting of a single constant buffer.
            _rootSignature = Device.CreateRootSignature(rootSigDesc.Serialize());
        }

        private void BuildShadersAndInputLayout()
        {
            _shaders["standardVS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "VS", "vs_5_0");
            _shaders["opaquePS"] = D3DUtil.CompileShader("Shaders\\Color.hlsl", "PS", "ps_5_0");

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
            });
        }

        private void BuildShapeGeometry()
        {
            GeometryGenerator.MeshData box = GeometryGenerator.CreateBox(1.5f, 0.5f, 1.5f, 3);
            GeometryGenerator.MeshData grid = GeometryGenerator.CreateGrid(20.0f, 30.0f, 60, 40);
            GeometryGenerator.MeshData sphere = GeometryGenerator.CreateSphere(0.5f, 20, 20);
            GeometryGenerator.MeshData cylinder = GeometryGenerator.CreateCylinder(0.5f, 0.3f, 3.0f, 20, 20);

            //
            // We are concatenating all the geometry into one big vertex/index buffer. So
            // define the regions in the buffer each submesh covers.
            //

            // Cache the vertex offsets to each object in the concatenated vertex buffer.
            int boxVertexOffset = 0;
            int gridVertexOffset = box.Vertices.Count;
            int sphereVertexOffset = gridVertexOffset + grid.Vertices.Count;
            int cylinderVertexOffset = sphereVertexOffset + sphere.Vertices.Count;

            // Cache the starting index for each object in the concatenated index buffer.
            int boxIndexOffset = 0;
            int gridIndexOffset = box.Indices32.Count;
            int sphereIndexOffset = gridIndexOffset + grid.Indices32.Count;
            int cylinderIndexOffset = sphereIndexOffset + sphere.Indices32.Count;

            // Define the SubmeshGeometry that cover different 
            // regions of the vertex/index buffers.

            var boxSubmesh = new SubmeshGeometry
            {
                IndexCount = box.Indices32.Count,
                StartIndexLocation = boxIndexOffset,
                BaseVertexLocation = boxVertexOffset
            };

            var gridSubmesh = new SubmeshGeometry
            {
                IndexCount = grid.Indices32.Count,
                StartIndexLocation = gridIndexOffset,
                BaseVertexLocation = gridVertexOffset
            };

            var sphereSubmesh = new SubmeshGeometry
            {
                IndexCount = sphere.Indices32.Count,
                StartIndexLocation = sphereIndexOffset,
                BaseVertexLocation = sphereVertexOffset
            };

            var cylinderSubmesh = new SubmeshGeometry
            {
                IndexCount = cylinder.Indices32.Count,
                StartIndexLocation = cylinderIndexOffset,
                BaseVertexLocation = cylinderVertexOffset
            };

            //
            // Extract the vertex elements we are interested in and pack the
            // vertices of all the meshes into one vertex buffer.
            //

            int totalVertexCount =
                box.Vertices.Count +
                grid.Vertices.Count +
                sphere.Vertices.Count +
                cylinder.Vertices.Count;

            var vertices = new Vertex[totalVertexCount];

            int k = 0;
            for (int i = 0; i < box.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = box.Vertices[i].Position;
                vertices[k].Color = Color.DarkGreen.ToVector4();
            }

            for (int i = 0; i < grid.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = grid.Vertices[i].Position;
                vertices[k].Color = Color.ForestGreen.ToVector4();
            }

            for (int i = 0; i < sphere.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = sphere.Vertices[i].Position;
                vertices[k].Color = Color.Crimson.ToVector4();
            }

            for (int i = 0; i < cylinder.Vertices.Count; ++i, ++k)
            {
                vertices[k].Pos = cylinder.Vertices[i].Position;
                vertices[k].Color = Color.SteelBlue.ToVector4();
            }

            var indices = new List<short>();
            indices.AddRange(box.GetIndices16());
            indices.AddRange(grid.GetIndices16());
            indices.AddRange(sphere.GetIndices16());
            indices.AddRange(cylinder.GetIndices16());

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");

            geo.DrawArgs["box"] = boxSubmesh;
            geo.DrawArgs["grid"] = gridSubmesh;
            geo.DrawArgs["sphere"] = sphereSubmesh;
            geo.DrawArgs["cylinder"] = cylinderSubmesh;

            _geometries[geo.Name] = geo;
        }

        private void BuildPSOs()
        {
            //
            // PSO for opaque objects.
            //

            var opaquePsoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _shaders["standardVS"],
                PixelShader = _shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePsoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _psos["opaque"] = Device.CreateGraphicsPipelineState(opaquePsoDesc);

            //
            // PSO for opaque wireframe objects.
            //

            var opaqueWireframePsoDesc = opaquePsoDesc;
            opaqueWireframePsoDesc.RasterizerState.FillMode = FillMode.Wireframe;

            _psos["opaque_wireframe"] = Device.CreateGraphicsPipelineState(opaqueWireframePsoDesc);
        }

        private void BuildFrameResources()
        {
            for (int i = 0; i < NumFrameResources; i++)
            {
                _frameResources.Add(new FrameResource(Device, 1, _allRitems.Count));
                _fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        private void BuildRenderItems()
        {
            var boxRitem = new RenderItem();
            boxRitem.World = Matrix.Scaling(2.0f, 2.0f, 2.0f) * Matrix.Translation(0.0f, 0.5f, 0.0f);
            boxRitem.ObjCBIndex = 0;
            boxRitem.Geo = _geometries["shapeGeo"];
            boxRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            boxRitem.IndexCount = boxRitem.Geo.DrawArgs["box"].IndexCount;
            boxRitem.StartIndexLocation = boxRitem.Geo.DrawArgs["box"].StartIndexLocation;
            boxRitem.BaseVertexLocation = boxRitem.Geo.DrawArgs["box"].BaseVertexLocation;
            _allRitems.Add(boxRitem);

            var gridRitem = new RenderItem();
            gridRitem.World = Matrix.Identity;
            gridRitem.ObjCBIndex = 1;
            gridRitem.Geo = _geometries["shapeGeo"];
            gridRitem.PrimitiveType = PrimitiveTopology.TriangleList;
            gridRitem.IndexCount = gridRitem.Geo.DrawArgs["grid"].IndexCount;
            gridRitem.StartIndexLocation = gridRitem.Geo.DrawArgs["grid"].StartIndexLocation;
            gridRitem.BaseVertexLocation = gridRitem.Geo.DrawArgs["grid"].BaseVertexLocation;
            _allRitems.Add(gridRitem);

            int objCBIndex = 2;
            for (int i = 0; i < 5; ++i)
            {
                var leftCylRitem = new RenderItem();
                var rightCylRitem = new RenderItem();
                var leftSphereRitem = new RenderItem();
                var rightSphereRitem = new RenderItem();

                leftCylRitem.World = Matrix.Translation(-5.0f, 1.5f, -10.0f + i * 5.0f);
                leftCylRitem.ObjCBIndex = objCBIndex++;
                leftCylRitem.Geo = _geometries["shapeGeo"];
                leftCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftCylRitem.IndexCount = leftCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                leftCylRitem.StartIndexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                leftCylRitem.BaseVertexLocation = leftCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;

                rightCylRitem.World = Matrix.Translation(+5.0f, 1.5f, -10.0f + i * 5.0f);
                rightCylRitem.ObjCBIndex = objCBIndex++;
                rightCylRitem.Geo = _geometries["shapeGeo"];
                rightCylRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightCylRitem.IndexCount = rightCylRitem.Geo.DrawArgs["cylinder"].IndexCount;
                rightCylRitem.StartIndexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].StartIndexLocation;
                rightCylRitem.BaseVertexLocation = rightCylRitem.Geo.DrawArgs["cylinder"].BaseVertexLocation;

                leftSphereRitem.World = Matrix.Translation(-5.0f, 3.5f, -10.0f + i * 5.0f);
                leftSphereRitem.ObjCBIndex = objCBIndex++;
                leftSphereRitem.Geo = _geometries["shapeGeo"];
                leftSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                leftSphereRitem.IndexCount = leftSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                leftSphereRitem.StartIndexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                leftSphereRitem.BaseVertexLocation = leftSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;

                rightSphereRitem.World = Matrix.Translation(+5.0f, 3.5f, -10.0f + i * 5.0f);
                rightSphereRitem.ObjCBIndex = objCBIndex++;
                rightSphereRitem.Geo = _geometries["shapeGeo"];
                rightSphereRitem.PrimitiveType = PrimitiveTopology.TriangleList;
                rightSphereRitem.IndexCount = rightSphereRitem.Geo.DrawArgs["sphere"].IndexCount;
                rightSphereRitem.StartIndexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].StartIndexLocation;
                rightSphereRitem.BaseVertexLocation = rightSphereRitem.Geo.DrawArgs["sphere"].BaseVertexLocation;

                _allRitems.Add(leftCylRitem);
                _allRitems.Add(rightCylRitem);
                _allRitems.Add(leftSphereRitem);
                _allRitems.Add(rightSphereRitem);
            }

            // All the render items are opaque.
            _opaqueRitems.AddRange(_allRitems);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, List<RenderItem> ritems)
        {
            // For each render item...
            foreach (RenderItem ri in ritems)
            {
                cmdList.SetVertexBuffer(0, ri.Geo.VertexBufferView);
                cmdList.SetIndexBuffer(ri.Geo.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;

                // Offset to the CBV in the descriptor heap for this object and for this frame resource.
                int cbvIndex = _currFrameResourceIndex * _opaqueRitems.Count + ri.ObjCBIndex;
                GpuDescriptorHandle cbvHandle = _cbvHeap.GPUDescriptorHandleForHeapStart;
                cbvHandle += cbvIndex * CbvSrvUavDescriptorSize;

                cmdList.SetGraphicsRootDescriptorTable(0, cbvHandle);

                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }
    }
}
