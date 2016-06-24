﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SiliconStudio.BuildEngine;
using SiliconStudio.Core;
using SiliconStudio.Core.Extensions;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Rendering;
using SiliconStudio.Core.Serialization;
using SiliconStudio.Core.Serialization.Assets;
using SiliconStudio.Xenko.Animations;
using SiliconStudio.Xenko.Shaders;
using System.Linq;

namespace SiliconStudio.Xenko.Assets.Model
{
    public abstract partial class ImportModelCommand : SingleFileImportCommand
    {
        private static int spawnedCommands;

        public ExportMode Mode { get; set; }

        public static ImportModelCommand Create(string extension)
        {
            if (ImportFbxCommand.IsSupportingExtensions(extension))
                return new ImportFbxCommand();
            if (ImportAssimpCommand.IsSupportingExtensions(extension))
                return new ImportAssimpCommand();

            return null;
        }

        protected ImportModelCommand()
        {
            // Set default values
            Mode = ExportMode.Model;
            AnimationRepeatMode = AnimationRepeatMode.LoopInfinite;
            ScaleImport = 1.0f;
        }

        private string ContextAsString => $"model [{Location}] from import [{SourcePath}]";

        /// <summary>
        /// The method to override containing the actual command code. It is called by the <see cref="DoCommand" /> function
        /// </summary>
        /// <param name="commandContext">The command context.</param>
        /// <returns>Task{ResultStatus}.</returns>
        protected override async Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
        {
            var assetManager = new ContentManager();

            while (Interlocked.Increment(ref spawnedCommands) >= 2)
            {
                Interlocked.Decrement(ref spawnedCommands);
                await Task.Delay(1, CancellationToken);
            }

            try
            {
                object exportedObject;

                switch (Mode)
                {
                    case ExportMode.Animation:
                        exportedObject = ExportAnimation(commandContext, assetManager);
                        break;
                    case ExportMode.Skeleton:
                        exportedObject = ExportSkeleton(commandContext, assetManager);
                        break;
                    case ExportMode.Model:
                        exportedObject = ExportModel(commandContext, assetManager);
                        break;
                    default:
                        commandContext.Logger.Error("Unknown export type [{0}] {1}", Mode, ContextAsString);
                        return ResultStatus.Failed;
                }

                if (exportedObject != null)
                    assetManager.Save(Location, exportedObject);

                commandContext.Logger.Verbose("The {0} has been successfully imported.", ContextAsString);

                return ResultStatus.Successful;
            }
            catch (Exception ex)
            {
                commandContext.Logger.Error("Unexpected error while importing {0}", ex, ContextAsString);
                return ResultStatus.Failed;
            }
            finally
            {
                Interlocked.Decrement(ref spawnedCommands);
            }
        }

        /// <summary>
        /// Get the transformation matrix to go from rootIndex to index.
        /// </summary>
        /// <param name="nodes">The nodes containing the local matrices.</param>
        /// <param name="rootIndex">The root index.</param>
        /// <param name="index">The current index.</param>
        /// <returns>The matrix at this index.</returns>
        private Matrix CombineMatricesFromNodeIndices(ModelNodeTransformation[] nodes, int rootIndex, int index)
        {
            if (index == -1 || index == rootIndex)
                return Matrix.Identity;

            var result = nodes[index].LocalMatrix;

            if (index != rootIndex)
            {
                var topMatrix = CombineMatricesFromNodeIndices(nodes, rootIndex, nodes[index].ParentIndex);
                result = Matrix.Multiply(result, topMatrix);
            }

            return result;
        }
        
        protected abstract Rendering.Model LoadModel(ICommandContext commandContext, ContentManager contentManager);

        protected abstract Dictionary<string, AnimationClip> LoadAnimation(ICommandContext commandContext, ContentManager contentManager, out TimeSpan duration);

        protected abstract Skeleton LoadSkeleton(ICommandContext commandContext, ContentManager contentManager);

        protected override void ComputeParameterHash(BinarySerializationWriter writer)
        {
            base.ComputeParameterHash(writer);

            writer.SerializeExtended(this, ArchiveMode.Serialize);
        }

        protected override IEnumerable<ObjectUrl> GetInputFilesImpl()
        {
            // Skeleton is a compile time dependency
            if (SkeletonUrl != null)
                yield return new ObjectUrl(UrlType.Content, SkeletonUrl);

            yield return new ObjectUrl(UrlType.File, SourcePath);
        }

        /// <summary>
        /// Compares the parameters of the two meshes.
        /// </summary>
        /// <param name="baseMesh">The base mesh.</param>
        /// <param name="newMesh">The mesh to compare.</param>
        /// <param name="extra">Unused parameter.</param>
        /// <returns>True if all the parameters are the same, false otherwise.</returns>
        private static bool CompareParameters(Rendering.Model model, Mesh baseMesh, Mesh newMesh)
        {
            var localParams = baseMesh.Parameters;
            if (localParams == null && newMesh.Parameters == null)
                return true;
            if (localParams == null || newMesh.Parameters == null)
                return false;
            return IsSubsetOf(localParams, newMesh.Parameters) && IsSubsetOf(newMesh.Parameters, localParams);
        }
        
        /// <summary>
        /// Compares the shadow options between the two meshes.
        /// </summary>
        /// <param name="baseMesh">The base mesh.</param>
        /// <param name="newMesh">The mesh to compare.</param>
        /// <param name="extra">Unused parameter.</param>
        /// <returns>True if the options are the same, false otherwise.</returns>
        private static bool CompareShadowOptions(Rendering.Model model, Mesh baseMesh, Mesh newMesh)
        {
            // TODO: Check is Model the same for the two mesh?
            var material1 = model.Materials.GetItemOrNull(baseMesh.MaterialIndex);
            var material2 = model.Materials.GetItemOrNull(newMesh.MaterialIndex);

            return material1 == material2 || (material1 != null && material2 != null && material1.IsShadowCaster == material2.IsShadowCaster &&
                material1.IsShadowReceiver == material2.IsShadowReceiver);
        }

        /// <summary>
        /// Test if two ParameterCollection are equal
        /// </summary>
        /// <param name="parameters0">The first ParameterCollection.</param>
        /// <param name="parameters1">The second ParameterCollection.</param>
        /// <returns>True if the collections are the same, false otherwise.</returns>
        private unsafe static bool IsSubsetOf(ParameterCollection parameters0, ParameterCollection parameters1)
        {
            foreach (var parameterKeyInfo in parameters0.ParameterKeyInfos)
            {
                var otherParameterKeyInfo = parameters1.ParameterKeyInfos.FirstOrDefault(x => x.Key == parameterKeyInfo.Key);

                // Nothing found?
                if (otherParameterKeyInfo.Key == null || parameterKeyInfo.Count != otherParameterKeyInfo.Count)
                    return false;

                if (parameterKeyInfo.Offset != -1)
                {
                    // Data
                    fixed (byte* dataValues0 = parameters0.DataValues)
                    fixed (byte* dataValues1 = parameters1.DataValues)
                        if (!Utilities.CompareMemory((IntPtr)dataValues0 + parameterKeyInfo.Offset, (IntPtr)dataValues1 + otherParameterKeyInfo.Offset, parameterKeyInfo.Count))
                            return false;
                }
                else if (parameterKeyInfo.BindingSlot != -1)
                {
                    // Resource
                    for (int i = 0; i < parameterKeyInfo.Count; ++i)
                    {
                        var object1 = parameters0.ObjectValues[parameterKeyInfo.BindingSlot + i];
                        var object2 = parameters1.ObjectValues[otherParameterKeyInfo.BindingSlot + i];
                        if (object1 == null && object2 == null)
                            continue;
                        if ((object1 == null && object2 != null) || (object2 == null && object1 != null))
                            return false;
                        if (object1.Equals(object2))
                            return false;
                    }
                }
            }
            return true;
        }

        public override string ToString()
        {
            return (SourcePath ?? "[File]") + " (" + Mode + ") > " + (Location ?? "[Location]");
        }

        public enum ExportMode
        {
            Skeleton,
            Model,
            Animation,
        }
    }
}