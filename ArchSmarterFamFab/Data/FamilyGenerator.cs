using System.Data;
using Autodesk.Revit.DB.Visual;

namespace ArchSmarterFamFab.Data
{
    public class GenerationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int ParameterCount { get; set; }
        public int RefPlaneCount { get; set; }
        public int GeometryCount { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class FamilyGenerator
    {
        private static readonly HashSet<string> KnownBuiltInParams = new HashSet<string>
        {
            "Family Name", "Keynote", "Assembly Code", "Type Comments", "Model",
            "Description", "Type IFC Predefined Type", "Type Image",
            "Export Type to IFC As", "Default Elevation", "Cost", "URL", "Manufacturer"
        };

        private static readonly string[] BuiltInRefPlaneNames =
        {
            "Center Left/Right",
            "Center (Left/Right)",
            "Center Front/Back",
            "Center (Front/Back)",
            "Ref. Level",
            "Ref Level",
            "Reference Plane",
            "Ceiling",
            "Exterior",
            "Interior",
            "Sill",
            "Wall Closure"
        };

        public GenerationResult Execute(Document doc, Autodesk.Revit.ApplicationServices.Application app, string familyJson, string texturePath = null)
        {
            var result = new GenerationResult();

            JsonDocument jsonDoc;
            try
            {
                jsonDoc = JsonDocument.Parse(familyJson);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to parse JSON: {ex.Message}";
                return result;
            }

            var root = jsonDoc.RootElement;

            string schemaVersion = root.GetProperty("metadata").GetProperty("schema_version").GetString();
            if (schemaVersion != "0.1")
            {
                result.ErrorMessage = $"Unsupported schema version: {schemaVersion}. Expected 0.1.";
                return result;
            }

            string units = root.GetProperty("units").GetString();

            foreach (var geom in root.GetProperty("geometry").EnumerateArray())
            {
                string geomType = geom.GetProperty("type").GetString();
                if (geomType != "extrusion" && geomType != "sweep" && geomType != "blend")
                {
                    result.ErrorMessage = $"Geometry type '{geomType}' is not supported. Only extrusions, sweeps, and blends are supported.";
                    return result;
                }
            }

            ForgeTypeId sourceUnitType = units switch
            {
                "inches" => UnitTypeId.Inches,
                "feet" => UnitTypeId.Feet,
                "millimeters" => UnitTypeId.Millimeters,
                "centimeters" => UnitTypeId.Centimeters,
                "meters" => UnitTypeId.Meters,
                _ => UnitTypeId.Inches
            };

            var paramDefaults = new Dictionary<string, double>();
            foreach (var param in root.GetProperty("parameters").EnumerateArray())
            {
                string paramName = param.GetProperty("name").GetString().Replace(" ", "_");
                string paramType = param.GetProperty("type").GetString();

                if (paramType == "length" || paramType == "number" || paramType == "angle" || paramType == "integer")
                {
                    double defaultVal = param.GetProperty("default_value").GetDouble();
                    paramDefaults[paramName] = defaultVal;
                }
            }

            ClearExistingContent(doc);

            using (Transaction trans = new Transaction(doc, "Generate Family from JSON"))
            {
                trans.Start();

                try
                {
                    // Step 1: Parameters
                    if (doc.FamilyManager.CurrentType == null)
                    {
                        string typeName = "Default";
                        if (root.TryGetProperty("metadata", out var meta) &&
                            meta.TryGetProperty("name", out var nameEl))
                        {
                            string n = nameEl.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(n)) typeName = n;
                        }
                        doc.FamilyManager.NewType(typeName);
                    }

                    var familyParams = new Dictionary<string, FamilyParameter>();
                    foreach (var param in root.GetProperty("parameters").EnumerateArray())
                    {
                        string paramName = param.GetProperty("name").GetString();
                        string paramType = param.GetProperty("type").GetString();
                        string instanceOrType = param.GetProperty("instance_or_type").GetString();
                        string group = param.GetProperty("group").GetString();
                        bool isInstance = instanceOrType == "instance";

                        ForgeTypeId specTypeId = MapParamTypeToSpec(paramType);
                        ForgeTypeId groupTypeId = MapParamGroupToGroupType(group);

                        try
                        {
                            FamilyParameter fp = doc.FamilyManager.AddParameter(
                                paramName, groupTypeId, specTypeId, isInstance);

                            if (paramType == "length")
                            {
                                double defaultInFeet = UnitUtils.ConvertToInternalUnits(
                                    param.GetProperty("default_value").GetDouble(), sourceUnitType);
                                doc.FamilyManager.Set(fp, defaultInFeet);
                            }
                            else if (paramType == "number" || paramType == "integer")
                            {
                                doc.FamilyManager.Set(fp, param.GetProperty("default_value").GetDouble());
                            }
                            else if (paramType == "yes_no")
                            {
                                doc.FamilyManager.Set(fp, param.GetProperty("default_value").GetBoolean() ? 1 : 0);
                            }
                            else if (paramType == "text")
                            {
                                doc.FamilyManager.Set(fp, param.GetProperty("default_value").GetString());
                            }

                            familyParams[paramName] = fp;
                            result.ParameterCount++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error creating parameter '{paramName}': {ex.Message}");
                            result.Errors.Add($"Parameter '{paramName}': {ex.Message}");
                        }
                    }

                    doc.Regenerate();

                    // Step 2: Reference Planes
                    View activeView = doc.ActiveView;
                    var refPlaneMap = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);

                    foreach (ReferencePlane rp in new FilteredElementCollector(doc)
                        .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    {
                        if (!string.IsNullOrEmpty(rp.Name))
                            refPlaneMap[rp.Name] = rp;
                    }

                    foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
                    {
                        string planeName = plane.GetProperty("name").GetString();
                        string direction = plane.GetProperty("direction").GetString();
                        string offsetExpr = GetExpressionString(plane.GetProperty("offset"));

                        double offsetValue = EvaluateExpression(offsetExpr, paramDefaults);
                        double offsetFeet = UnitUtils.ConvertToInternalUnits(offsetValue, sourceUnitType);

                        XYZ bubbleEnd, freeEnd, cutVector;
                        switch (direction)
                        {
                            case "x":
                                bubbleEnd = new XYZ(offsetFeet, 0, 0);
                                freeEnd = new XYZ(offsetFeet, 0, 4);
                                cutVector = new XYZ(0, 1, 0);
                                break;
                            case "y":
                                bubbleEnd = new XYZ(0, offsetFeet, 0);
                                freeEnd = new XYZ(0, offsetFeet, 4);
                                cutVector = new XYZ(1, 0, 0);
                                break;
                            case "z":
                                bubbleEnd = new XYZ(0, 0, offsetFeet);
                                freeEnd = new XYZ(4, 0, offsetFeet);
                                cutVector = new XYZ(0, 1, 0);
                                break;
                            default:
                                continue;
                        }

                        if (refPlaneMap.ContainsKey(planeName))
                        {
                            // Reuse the existing template plane — just count it
                            result.RefPlaneCount++;
                        }
                        else
                        {
                            try
                            {
                                ReferencePlane newPlane = doc.FamilyCreate.NewReferencePlane(
                                    bubbleEnd, freeEnd, cutVector, activeView);
                                newPlane.Name = planeName;
                                refPlaneMap[planeName] = newPlane;
                                result.RefPlaneCount++;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error creating ref plane '{planeName}': {ex.Message}");
                                result.Errors.Add($"Ref plane '{planeName}': {ex.Message}");
                            }
                        }
                    }

                    doc.Regenerate();

                    // Step 3: Subcategories
                    var subcategoryMap = new Dictionary<string, Category>();
                    if (root.TryGetProperty("subcategories", out var subcatsElement))
                    {
                        Category parentCategory = doc.OwnerFamily.FamilyCategory;
                        foreach (var subcat in subcatsElement.EnumerateArray())
                        {
                            string subcatName = subcat.GetString();
                            try
                            {
                                Category existing = parentCategory.SubCategories
                                    .Cast<Category>()
                                    .FirstOrDefault(c => c.Name == subcatName);

                                subcategoryMap[subcatName] = existing ??
                                    doc.Settings.Categories.NewSubcategory(parentCategory, subcatName);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Subcategory '{subcatName}': {ex.Message}");
                            }
                        }
                    }

                    // Step 4: Geometry
                    foreach (var geom in root.GetProperty("geometry").EnumerateArray())
                    {
                        string geomName = geom.GetProperty("name").GetString();
                        string geomType = geom.GetProperty("type").GetString();
                        string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

                        if (!refPlaneMap.ContainsKey(sketchPlaneName))
                        {
                            result.Errors.Add($"'{geomName}': sketch plane '{sketchPlaneName}' not found");
                            continue;
                        }

                        ReferencePlane sketchRefPlane = refPlaneMap[sketchPlaneName];
                        string planeDirection = GetPlaneDirection(sketchPlaneName, root);

                        bool created = false;
                        string error = null;

                        if (geomType == "extrusion")
                        {
                            created = CreateExtrusion(doc, geom, sketchRefPlane, planeDirection,
                                root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                        }
                        else if (geomType == "sweep")
                        {
                            created = CreateSweep(doc, app, geom, sketchRefPlane, planeDirection,
                                root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                        }
                        else if (geomType == "blend")
                        {
                            created = CreateBlend(doc, app, geom, sketchRefPlane, planeDirection,
                                root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                        }

                        if (created)
                            result.GeometryCount++;
                        else if (error != null)
                            result.Errors.Add($"'{geomName}': {error}");
                    }

                    // Step 5: Optional photo texture material
                    if (!string.IsNullOrEmpty(texturePath) && File.Exists(texturePath) && result.GeometryCount > 0)
                    {
                        try
                        {
                            ElementId materialId = CreateTexturedMaterial(doc, root, texturePath);
                            if (materialId != null && materialId != ElementId.InvalidElementId)
                            {
                                int applied = ApplyMaterialToSolids(doc, materialId);
                                if (applied == 0)
                                    result.Errors.Add("Texture: material created but no solid geometry accepted it.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Texture material failed: {ex.Message}");
                            result.Errors.Add($"Texture: {ex.Message}");
                        }
                    }

                    doc.Regenerate();
                    trans.Commit();
                    result.Success = true;
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    result.ErrorMessage = $"Family generation failed: {ex.Message}";
                }
            }

            result.ErrorCount = result.Errors.Count;
            return result;
        }

        private static int ApplyMaterialToSolids(Document doc, ElementId materialId)
        {
            var forms = new List<Element>();
            forms.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).ToElements());
            forms.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Sweep)).ToElements());
            forms.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Blend)).ToElements());

            int applied = 0;
            foreach (Element form in forms)
            {
                if (form is GenericForm gf && !gf.IsSolid) continue; // voids carry no material
                Parameter mp = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (mp != null && !mp.IsReadOnly && mp.StorageType == StorageType.ElementId)
                {
                    mp.Set(materialId);
                    applied++;
                }
            }
            return applied;
        }

        private static ElementId CreateTexturedMaterial(Document doc, JsonElement root, string texturePath)
        {
            string baseName = "FamFab Photo";
            if (root.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("name", out var nm) &&
                nm.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nm.GetString()))
            {
                baseName = "FamFab " + nm.GetString().Trim();
            }

            string materialName = UniqueElementName(doc, typeof(Material), baseName);
            ElementId materialId = Material.Create(doc, materialName);
            Material material = doc.GetElement(materialId) as Material;
            if (material == null) return ElementId.InvalidElementId;

            // Clone an existing "Generic" appearance asset, then point its diffuse channel
            // at the bitmap. Requires an appearance asset to exist in the document.
            AppearanceAssetElement templateAsset = new FilteredElementCollector(doc)
                .OfClass(typeof(AppearanceAssetElement))
                .Cast<AppearanceAssetElement>()
                .FirstOrDefault(a => a.Name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(AppearanceAssetElement))
                    .Cast<AppearanceAssetElement>()
                    .FirstOrDefault();

            if (templateAsset == null)
                return materialId; // No appearance asset to clone; material keeps its default shading.

            AppearanceAssetElement newAsset = templateAsset.Duplicate(
                UniqueElementName(doc, typeof(AppearanceAssetElement), materialName + " Appearance"));
            material.AppearanceAssetId = newAsset.Id;

            using (AppearanceAssetEditScope editScope = new AppearanceAssetEditScope(doc))
            {
                Asset editableAsset = editScope.Start(newAsset.Id);
                AssetProperty diffuse = editableAsset.FindByName("generic_diffuse");
                if (diffuse != null)
                {
                    if (diffuse.NumberOfConnectedProperties == 0)
                        diffuse.AddConnectedAsset("UnifiedBitmapSchema");

                    if (diffuse.NumberOfConnectedProperties > 0 &&
                        diffuse.GetConnectedProperty(0) is Asset connected &&
                        connected.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString bitmap &&
                        bitmap.IsValidValue(texturePath))
                    {
                        bitmap.Value = texturePath;
                    }
                }
                editScope.Commit(true);
            }

            return materialId;
        }

        private static string UniqueElementName(Document doc, Type elementType, string desired)
        {
            var used = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(elementType)
                    .Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)),
                StringComparer.OrdinalIgnoreCase);

            if (!used.Contains(desired)) return desired;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = desired + " " + i;
                if (!used.Contains(candidate)) return candidate;
            }
            return desired + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private void ClearExistingContent(Document doc)
        {
            var existingExtrusions = new FilteredElementCollector(doc)
                .OfClass(typeof(Extrusion)).ToElements();
            var existingSweeps = new FilteredElementCollector(doc)
                .OfClass(typeof(Sweep)).ToElements();
            var existingBlends = new FilteredElementCollector(doc)
                .OfClass(typeof(Blend)).ToElements();
            var existingRefPlanes = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Where(rp => !BuiltInRefPlaneNames.Contains(rp.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            bool needsClear = existingExtrusions.Count > 0 || existingSweeps.Count > 0
                || existingBlends.Count > 0 || existingRefPlanes.Count > 0;

            if (!needsClear) return;

            using (Transaction t = new Transaction(doc, "Clear existing family content"))
            {
                t.Start();

                foreach (var sw in existingSweeps)
                    try { doc.Delete(sw.Id); } catch { }

                foreach (var bl in existingBlends)
                    try { doc.Delete(bl.Id); } catch { }

                foreach (var ext in existingExtrusions)
                    try { doc.Delete(ext.Id); } catch { }

                foreach (var rp in existingRefPlanes)
                    try { doc.Delete(rp.Id); } catch { }

                var paramsToDelete = new List<FamilyParameter>();
                foreach (FamilyParameter fp in doc.FamilyManager.Parameters)
                {
                    if (fp.IsReadOnly || KnownBuiltInParams.Contains(fp.Definition.Name))
                        continue;
                    paramsToDelete.Add(fp);
                }
                foreach (var fp in paramsToDelete)
                    try { doc.FamilyManager.RemoveParameter(fp); } catch { }

                doc.Regenerate();
                t.Commit();
            }
        }

        // ==================== GEOMETRY CREATORS ====================

        private static bool CreateExtrusion(
            Document doc, JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            string geomName = geom.GetProperty("name").GetString();
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            SketchPlane sketchPlane;
            try
            {
                XYZ planeOrigin = sketchRefPlane.GetPlane().Origin;
                XYZ expectedNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };
                Plane plane = Plane.CreateByNormalAndOrigin(expectedNormal, planeOrigin);
                sketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane from '{sketchPlaneName}': {ex.Message}";
                return false;
            }

            var profile = geom.GetProperty("profile");
            CurveArrArray curveArrArray = new CurveArrArray();
            CurveArray curveArray = new CurveArray();

            string shape = profile.GetProperty("shape").GetString();
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            try
            {
                switch (shape)
                {
                    case "rectangle":
                        BuildRectangleProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    case "circle":
                        BuildCircleProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    case "custom":
                        BuildCustomProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    default:
                        error = $"Profile shape '{shape}' not supported.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Profile error: {ex.Message}";
                return false;
            }

            curveArrArray.Append(curveArray);

            string startExpr = GetExpressionString(geom.GetProperty("extrusion_start"));
            string endExpr = GetExpressionString(geom.GetProperty("extrusion_end"));
            double startValue = EvaluateExpression(startExpr, paramDefaults);
            double endValue = EvaluateExpression(endExpr, paramDefaults);

            double startFeet = UnitUtils.ConvertToInternalUnits(startValue, sourceUnitType);
            double endFeet = UnitUtils.ConvertToInternalUnits(endValue, sourceUnitType);

            double creationEnd = Math.Abs(endFeet - startFeet);
            if (creationEnd < 0.001) creationEnd = 0.01;

            try
            {
                Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                    !isVoid, curveArrArray, sketchPlane, creationEnd);
                extrusion.StartOffset = startFeet;
                extrusion.EndOffset = endFeet;

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        extrusion.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Extrusion failed: {ex.Message}";
                return false;
            }
        }

        private static bool CreateSweep(
            Document doc, Autodesk.Revit.ApplicationServices.Application app,
            JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            string geomName = geom.GetProperty("name").GetString();
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            SketchPlane pathSketchPlane;
            try
            {
                XYZ planeOrigin = sketchRefPlane.GetPlane().Origin;
                XYZ expectedNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };
                Plane plane = Plane.CreateByNormalAndOrigin(expectedNormal, planeOrigin);
                pathSketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane for sweep path: {ex.Message}";
                return false;
            }

            var pathProp = geom.GetProperty("path");
            var segments = pathProp.GetProperty("segments");
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            var pathCurves3D = new List<Curve>();
            Curve previousCurve = null;

            foreach (var seg in segments.EnumerateArray())
            {
                string segType = seg.GetProperty("segment_type").GetString();
                try
                {
                    if (segType == "line")
                    {
                        var startPt = seg.GetProperty("start");
                        var endPt = seg.GetProperty("end");
                        double su = EvaluateExpression(GetExpressionString(startPt.GetProperty("u")), paramDefaults);
                        double sv = EvaluateExpression(GetExpressionString(startPt.GetProperty("v")), paramDefaults);
                        double eu = EvaluateExpression(GetExpressionString(endPt.GetProperty("u")), paramDefaults);
                        double ev = EvaluateExpression(GetExpressionString(endPt.GetProperty("v")), paramDefaults);

                        XYZ start3D = UVToXYZ(su, sv, planeDirection, sourceUnitType, planeOffsetFeet);
                        XYZ end3D = UVToXYZ(eu, ev, planeDirection, sourceUnitType, planeOffsetFeet);

                        Line line = Line.CreateBound(start3D, end3D);
                        pathCurves3D.Add(line);
                        previousCurve = line;
                    }
                    else if (segType == "arc")
                    {
                        var startPt = seg.GetProperty("start");
                        var endPt = seg.GetProperty("end");
                        var centerPt = seg.GetProperty("center");

                        double su = EvaluateExpression(GetExpressionString(startPt.GetProperty("u")), paramDefaults);
                        double sv = EvaluateExpression(GetExpressionString(startPt.GetProperty("v")), paramDefaults);
                        double eu = EvaluateExpression(GetExpressionString(endPt.GetProperty("u")), paramDefaults);
                        double ev = EvaluateExpression(GetExpressionString(endPt.GetProperty("v")), paramDefaults);
                        double cu = EvaluateExpression(GetExpressionString(centerPt.GetProperty("u")), paramDefaults);
                        double cv = EvaluateExpression(GetExpressionString(centerPt.GetProperty("v")), paramDefaults);

                        double inTanU = 0, inTanV = 0;
                        if (previousCurve != null)
                        {
                            XYZ tangent3D = previousCurve.ComputeDerivatives(1.0, true).BasisX.Normalize();
                            ExtractUV(tangent3D, planeDirection, out inTanU, out inTanV);
                        }
                        else
                        {
                            double dsu = su - cu, dsv = sv - cv;
                            inTanU = -dsv;
                            inTanV = dsu;
                        }

                        XYZ passThroughPoint = ComputeArcPassThrough(
                            su, sv, eu, ev, cu, cv, inTanU, inTanV,
                            planeDirection, sourceUnitType, planeOffsetFeet);

                        XYZ start3D = UVToXYZ(su, sv, planeDirection, sourceUnitType, planeOffsetFeet);
                        XYZ end3D = UVToXYZ(eu, ev, planeDirection, sourceUnitType, planeOffsetFeet);

                        Arc arc = Arc.Create(start3D, end3D, passThroughPoint);
                        pathCurves3D.Add(arc);
                        previousCurve = arc;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error building path segment: {ex.Message}");
                }
            }

            if (pathCurves3D.Count == 0)
            {
                error = "No valid path curves for sweep.";
                return false;
            }

            var modelCurves = new List<ModelCurve>();
            try
            {
                foreach (var curve in pathCurves3D)
                    modelCurves.Add(doc.FamilyCreate.NewModelCurve(curve, pathSketchPlane));
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                error = $"Error creating model curves: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            ReferenceArray pathRefArray = new ReferenceArray();
            bool refsOk = true;
            foreach (var mc in modelCurves)
            {
                try
                {
                    Reference curveRef = mc.GeometryCurve.Reference;
                    if (curveRef != null)
                    {
                        pathRefArray.Append(curveRef);
                    }
                    else
                    {
                        Options opts = new Options { ComputeReferences = true };
                        GeometryElement geomElem = mc.get_Geometry(opts);
                        bool found = false;
                        foreach (GeometryObject geomObj in geomElem)
                        {
                            if (geomObj is Curve c && c.Reference != null)
                            {
                                pathRefArray.Append(c.Reference);
                                found = true;
                                break;
                            }
                        }
                        if (!found) refsOk = false;
                    }
                }
                catch { refsOk = false; }
            }

            if (!refsOk || pathRefArray.Size == 0)
            {
                error = "Could not collect path references for sweep.";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            var profileProp = geom.GetProperty("profile");
            string profileShape = profileProp.GetProperty("shape").GetString();

            CurveArrArray sweepProfileArray = new CurveArrArray();
            CurveArray sweepProfileCurves = new CurveArray();

            try
            {
                Curve firstPathCurve = pathCurves3D[0];
                XYZ pathStartPoint = firstPathCurve.GetEndPoint(0);
                XYZ pathTangent = firstPathCurve.ComputeDerivatives(0, true).BasisX.Normalize();

                XYZ sketchNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };

                XYZ profileU = sketchNormal;
                XYZ profileV = pathTangent.CrossProduct(profileU).Normalize();

                if (profileV.GetLength() < 0.001)
                {
                    profileV = XYZ.BasisZ;
                    if (Math.Abs(profileV.DotProduct(pathTangent)) > 0.99)
                        profileV = XYZ.BasisY;
                    profileV = pathTangent.CrossProduct(profileU);
                    if (profileV.GetLength() < 0.001)
                        profileV = XYZ.BasisZ;
                    else
                        profileV = profileV.Normalize();
                }

                switch (profileShape)
                {
                    case "rectangle":
                        BuildSweepRectangleProfile(profileProp, paramDefaults, sourceUnitType,
                            pathStartPoint, profileU, profileV, sweepProfileCurves);
                        break;
                    case "circle":
                        BuildSweepCircleProfile(profileProp, paramDefaults, sourceUnitType,
                            pathStartPoint, profileU, profileV, sweepProfileCurves);
                        break;
                    default:
                        error = $"Sweep profile shape '{profileShape}' not supported.";
                        foreach (var mc in modelCurves)
                            try { doc.Delete(mc.Id); } catch { }
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Error building sweep profile: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            sweepProfileArray.Append(sweepProfileCurves);

            try
            {
                SweepProfile sweepProfile = app.Create.NewCurveLoopsProfile(sweepProfileArray);
                Sweep sweep = doc.FamilyCreate.NewSweep(
                    !isVoid, pathRefArray, sweepProfile, 0, ProfilePlaneLocation.Start);

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        sweep.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Sweep failed: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }
        }

        private static bool CreateBlend(
            Document doc, Autodesk.Revit.ApplicationServices.Application app,
            JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            string geomName = geom.GetProperty("name").GetString();
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            var bottomProfile = geom.GetProperty("bottom_profile");
            var topProfile = geom.GetProperty("top_profile");
            string bottomShape = bottomProfile.GetProperty("shape").GetString();
            string topShape = topProfile.GetProperty("shape").GetString();

            string bottomOffsetExpr = GetExpressionString(geom.GetProperty("bottom_offset"));
            string topOffsetExpr = GetExpressionString(geom.GetProperty("top_offset"));
            double bottomOffsetValue = EvaluateExpression(bottomOffsetExpr, paramDefaults);
            double topOffsetValue = EvaluateExpression(topOffsetExpr, paramDefaults);

            if (bottomShape == "circle" && topShape == "circle")
            {
                double bottomRadius = EvaluateExpression(
                    GetExpressionString(bottomProfile.GetProperty("radius")), paramDefaults);
                double topRadius = EvaluateExpression(
                    GetExpressionString(topProfile.GetProperty("radius")), paramDefaults);

                if (Math.Abs(bottomRadius - topRadius) < 0.001)
                {
                    return CreateBlendAsSweep(doc, geom, sketchRefPlane, planeDirection,
                        root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                }
            }

            SketchPlane sketchPlane;
            try
            {
                XYZ planeOrigin = sketchRefPlane.GetPlane().Origin;
                XYZ expectedNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };
                Plane plane = Plane.CreateByNormalAndOrigin(expectedNormal, planeOrigin);
                sketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane from '{sketchPlaneName}': {ex.Message}";
                return false;
            }

            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            CurveArray bottomCurves = new CurveArray();
            CurveArray topCurves = new CurveArray();

            try
            {
                switch (bottomShape)
                {
                    case "rectangle":
                        BuildRectangleProfile(bottomProfile, planeDirection, paramDefaults, sourceUnitType, bottomCurves, planeOffsetFeet);
                        break;
                    case "circle":
                        BuildCircleAsPolygonProfile(bottomProfile, planeDirection, paramDefaults, sourceUnitType, bottomCurves, planeOffsetFeet);
                        break;
                    case "custom":
                        BuildCustomProfile(bottomProfile, planeDirection, paramDefaults, sourceUnitType, bottomCurves, planeOffsetFeet);
                        break;
                    default:
                        error = $"Bottom profile shape '{bottomShape}' not supported.";
                        return false;
                }

                switch (topShape)
                {
                    case "rectangle":
                        BuildRectangleProfile(topProfile, planeDirection, paramDefaults, sourceUnitType, topCurves, planeOffsetFeet);
                        break;
                    case "circle":
                        BuildCircleAsPolygonProfile(topProfile, planeDirection, paramDefaults, sourceUnitType, topCurves, planeOffsetFeet);
                        break;
                    case "custom":
                        BuildCustomProfile(topProfile, planeDirection, paramDefaults, sourceUnitType, topCurves, planeOffsetFeet);
                        break;
                    default:
                        error = $"Top profile shape '{topShape}' not supported.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Profile error: {ex.Message}";
                return false;
            }

            if (bottomCurves.Size != topCurves.Size)
            {
                error = $"Blend profiles must have the same number of curve segments. Bottom has {bottomCurves.Size}, top has {topCurves.Size}.";
                return false;
            }

            double bottomOffsetFeet = UnitUtils.ConvertToInternalUnits(bottomOffsetValue, sourceUnitType);
            double topOffsetFeet = UnitUtils.ConvertToInternalUnits(topOffsetValue, sourceUnitType);

            doc.Regenerate();

            Blend blend;
            try
            {
                blend = doc.FamilyCreate.NewBlend(!isVoid, topCurves, bottomCurves, sketchPlane);
            }
            catch (Exception ex)
            {
                error = $"NewBlend call failed ({ex.GetType().Name}): {ex.Message}" +
                    $" | bottomCurves={bottomCurves.Size}, topCurves={topCurves.Size}" +
                    $" | bottomOffset={bottomOffsetValue}{(sourceUnitType == UnitTypeId.Inches ? "in" : "")}" +
                    $" | topOffset={topOffsetValue}";
                if (ex.InnerException != null)
                    error += $" | Inner: {ex.InnerException.Message}";
                return false;
            }

            try
            {
                if (topOffsetFeet >= bottomOffsetFeet)
                {
                    blend.TopOffset = topOffsetFeet;
                    blend.BottomOffset = bottomOffsetFeet;
                }
                else
                {
                    blend.BottomOffset = bottomOffsetFeet;
                    blend.TopOffset = topOffsetFeet;
                }
            }
            catch (Exception ex)
            {
                error = $"Blend offset failed ({ex.GetType().Name}): {ex.Message}" +
                    $" | bottomOffset={bottomOffsetFeet:F4}ft, topOffset={topOffsetFeet:F4}ft";
                return false;
            }

            if (geom.TryGetProperty("subcategory", out var subcatProp))
            {
                string subcatName = subcatProp.GetString();
                if (subcategoryMap.ContainsKey(subcatName))
                    blend.Subcategory = subcategoryMap[subcatName];
            }

            return true;
        }

        private static bool CreateBlendAsSweep(
            Document doc, JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            var bottomProfile = geom.GetProperty("bottom_profile");
            var topProfile = geom.GetProperty("top_profile");

            var bottomCenter = bottomProfile.GetProperty("center");
            double bcu = EvaluateExpression(GetExpressionString(bottomCenter.GetProperty("u")), paramDefaults);
            double bcv = EvaluateExpression(GetExpressionString(bottomCenter.GetProperty("v")), paramDefaults);

            var topCenter = topProfile.GetProperty("center");
            double tcu = EvaluateExpression(GetExpressionString(topCenter.GetProperty("u")), paramDefaults);
            double tcv = EvaluateExpression(GetExpressionString(topCenter.GetProperty("v")), paramDefaults);

            double radius = EvaluateExpression(
                GetExpressionString(bottomProfile.GetProperty("radius")), paramDefaults);

            string bottomOffsetExpr = GetExpressionString(geom.GetProperty("bottom_offset"));
            string topOffsetExpr = GetExpressionString(geom.GetProperty("top_offset"));
            double bottomOffsetValue = EvaluateExpression(bottomOffsetExpr, paramDefaults);
            double topOffsetValue = EvaluateExpression(topOffsetExpr, paramDefaults);
            double bottomOffsetFeet = UnitUtils.ConvertToInternalUnits(bottomOffsetValue, sourceUnitType);
            double topOffsetFeet = UnitUtils.ConvertToInternalUnits(topOffsetValue, sourceUnitType);

            XYZ bottomPoint = UVToXYZ(bcu, bcv, planeDirection, sourceUnitType, planeOffsetFeet + bottomOffsetFeet);
            XYZ topPoint = UVToXYZ(tcu, tcv, planeDirection, sourceUnitType, planeOffsetFeet + topOffsetFeet);

            double cableLength = bottomPoint.DistanceTo(topPoint);
            if (cableLength < 0.01)
            {
                error = "Cable too short (bottom and top points nearly coincide).";
                return false;
            }

            XYZ cableDir = (topPoint - bottomPoint).Normalize();

            Plane extPlane = Plane.CreateByNormalAndOrigin(cableDir, bottomPoint);
            SketchPlane extSketchPlane;
            try
            {
                extSketchPlane = SketchPlane.Create(doc, extPlane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane for extrusion failed: {ex.Message}";
                return false;
            }

            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, sourceUnitType);
            XYZ basisU = extPlane.XVec;
            XYZ basisV = extPlane.YVec;

            CurveArrArray profileLoops = new CurveArrArray();
            CurveArray profileCurves = new CurveArray();

            try
            {
                XYZ circleTop = bottomPoint.Add(basisV.Multiply(radiusFeet));
                XYZ circleBottom = bottomPoint.Add(basisV.Multiply(-radiusFeet));
                XYZ circleRight = bottomPoint.Add(basisU.Multiply(radiusFeet));
                XYZ circleLeft = bottomPoint.Add(basisU.Multiply(-radiusFeet));

                profileCurves.Append(Arc.Create(circleTop, circleBottom, circleRight));
                profileCurves.Append(Arc.Create(circleBottom, circleTop, circleLeft));
            }
            catch (Exception ex)
            {
                error = $"Circle profile failed: {ex.Message}";
                return false;
            }

            profileLoops.Append(profileCurves);

            try
            {
                Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                    !isVoid, profileLoops, extSketchPlane, cableLength);

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        extrusion.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Extrusion failed ({ex.GetType().Name}): {ex.Message}";
                if (ex.InnerException != null)
                    error += $" | Inner: {ex.InnerException.Message}";
                return false;
            }
        }

        // ==================== EXPRESSION HANDLING ====================

        private static string GetExpressionString(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetDouble().ToString();
            return element.GetString();
        }

        private static double EvaluateExpression(string expr, Dictionary<string, double> defaults)
        {
            if (string.IsNullOrWhiteSpace(expr)) return 0;
            if (double.TryParse(expr.Trim(), out double literal)) return literal;

            string evaluated = expr;
            foreach (var kvp in defaults.OrderByDescending(k => k.Key.Length))
                evaluated = evaluated.Replace(kvp.Key, kvp.Value.ToString());

            try
            {
                var dt = new DataTable();
                object result = dt.Compute(evaluated, "");
                return Convert.ToDouble(result);
            }
            catch
            {
                return 0;
            }
        }

        // ==================== REFERENCE PLANE HELPERS ====================

        private static string GetPlaneDirection(string planeName, JsonElement root)
        {
            foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
            {
                if (string.Equals(plane.GetProperty("name").GetString(), planeName, StringComparison.OrdinalIgnoreCase))
                    return plane.GetProperty("direction").GetString();
            }

            if (planeName.Contains("Left/Right", StringComparison.OrdinalIgnoreCase)) return "x";
            if (planeName.Contains("Front/Back", StringComparison.OrdinalIgnoreCase)) return "y";
            if (planeName.Contains("Ref. Level", StringComparison.OrdinalIgnoreCase) ||
                planeName.Contains("Ref Level", StringComparison.OrdinalIgnoreCase)) return "z";
            return "z";
        }

        private static double GetPlaneOffsetFeet(
            string planeName, JsonElement root,
            Dictionary<string, double> defaults, ForgeTypeId sourceUnitType)
        {
            foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
            {
                if (string.Equals(plane.GetProperty("name").GetString(), planeName, StringComparison.OrdinalIgnoreCase))
                {
                    string offsetExpr = GetExpressionString(plane.GetProperty("offset"));
                    double offsetInJsonUnits = EvaluateExpression(offsetExpr, defaults);
                    return UnitUtils.ConvertToInternalUnits(offsetInJsonUnits, sourceUnitType);
                }
            }
            return 0;
        }

        // ==================== PARAMETER TYPE MAPPING ====================

        private static ForgeTypeId MapParamTypeToSpec(string paramType)
        {
            return paramType switch
            {
                "length" => SpecTypeId.Length,
                "angle" => SpecTypeId.Angle,
                "number" => SpecTypeId.Number,
                "integer" => SpecTypeId.Int.Integer,
                "yes_no" => SpecTypeId.Boolean.YesNo,
                "text" => SpecTypeId.String.Text,
                "material" => SpecTypeId.Reference.Material,
                _ => SpecTypeId.String.Text
            };
        }

        private static ForgeTypeId MapParamGroupToGroupType(string group)
        {
            return group switch
            {
                "Dimensions" => GroupTypeId.Geometry,
                "Construction" => GroupTypeId.Construction,
                "Identity Data" => GroupTypeId.IdentityData,
                "Graphics" => GroupTypeId.Graphics,
                "Materials and Finishes" => GroupTypeId.Materials,
                _ => GroupTypeId.General
            };
        }

        // ==================== COORDINATE HELPERS ====================

        private static XYZ UVToXYZ(double u, double v, string planeDirection, ForgeTypeId unitType, double planeOffsetFeet)
        {
            double uFeet = UnitUtils.ConvertToInternalUnits(u, unitType);
            double vFeet = UnitUtils.ConvertToInternalUnits(v, unitType);

            return planeDirection switch
            {
                "x" => new XYZ(planeOffsetFeet, uFeet, vFeet),
                "y" => new XYZ(uFeet, planeOffsetFeet, vFeet),
                "z" => new XYZ(uFeet, vFeet, planeOffsetFeet),
                _ => new XYZ(uFeet, vFeet, planeOffsetFeet)
            };
        }

        private static void ExtractUV(XYZ vec, string planeDirection, out double u, out double v)
        {
            switch (planeDirection)
            {
                case "x": u = vec.Y; v = vec.Z; break;
                case "y": u = vec.X; v = vec.Z; break;
                case "z": u = vec.X; v = vec.Y; break;
                default: u = vec.X; v = vec.Y; break;
            }
        }

        // ==================== ARC CALCULATION ====================

        private static XYZ ComputeArcPassThrough(
            double su, double sv, double eu, double ev,
            double cu, double cv, double inTanU, double inTanV,
            string planeDirection, ForgeTypeId unitType, double planeOffsetFeet)
        {
            double radius = Math.Sqrt((su - cu) * (su - cu) + (sv - cv) * (sv - cv));
            double mu = (su + eu) / 2.0;
            double mv = (sv + ev) / 2.0;
            double dmu = mu - cu;
            double dmv = mv - cv;
            double dmLen = Math.Sqrt(dmu * dmu + dmv * dmv);

            double mid1u, mid1v, mid2u, mid2v;

            if (dmLen < 0.001)
            {
                double chu = eu - su, chv = ev - sv;
                double chLen = Math.Sqrt(chu * chu + chv * chv);
                if (chLen < 0.001)
                    return UVToXYZ(cu, cv, planeDirection, unitType, planeOffsetFeet);

                double perpU = -chv / chLen, perpV = chu / chLen;
                mid1u = cu + perpU * radius;
                mid1v = cv + perpV * radius;
                mid2u = cu - perpU * radius;
                mid2v = cv - perpV * radius;
            }
            else
            {
                double dirU = dmu / dmLen, dirV = dmv / dmLen;
                mid1u = cu + dirU * radius;
                mid1v = cv + dirV * radius;
                mid2u = cu - dirU * radius;
                mid2v = cv - dirV * radius;
            }

            double csu = su - cu, csv = sv - cv;
            double perpAu = -csv, perpAv = csu;
            double dotToMid1 = perpAu * (mid1u - su) + perpAv * (mid1v - sv);
            double tangentForMid1U = dotToMid1 >= 0 ? perpAu : -perpAu;
            double tangentForMid1V = dotToMid1 >= 0 ? perpAv : -perpAv;
            double alignment = tangentForMid1U * inTanU + tangentForMid1V * inTanV;

            double chosenU = alignment >= 0 ? mid1u : mid2u;
            double chosenV = alignment >= 0 ? mid1v : mid2v;

            return UVToXYZ(chosenU, chosenV, planeDirection, unitType, planeOffsetFeet);
        }

        // ==================== EXTRUSION PROFILE BUILDERS ====================

        private static void BuildRectangleProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var origin = profile.GetProperty("origin");
            double ou = EvaluateExpression(GetExpressionString(origin.GetProperty("u")), defaults);
            double ov = EvaluateExpression(GetExpressionString(origin.GetProperty("v")), defaults);
            double w = EvaluateExpression(GetExpressionString(profile.GetProperty("width")), defaults);
            double h = EvaluateExpression(GetExpressionString(profile.GetProperty("height")), defaults);

            XYZ p0 = UVToXYZ(ou, ov, planeDirection, unitType, planeOffsetFeet);
            XYZ p1 = UVToXYZ(ou + w, ov, planeDirection, unitType, planeOffsetFeet);
            XYZ p2 = UVToXYZ(ou + w, ov + h, planeDirection, unitType, planeOffsetFeet);
            XYZ p3 = UVToXYZ(ou, ov + h, planeDirection, unitType, planeOffsetFeet);

            curves.Append(Line.CreateBound(p0, p1));
            curves.Append(Line.CreateBound(p1, p2));
            curves.Append(Line.CreateBound(p2, p3));
            curves.Append(Line.CreateBound(p3, p0));
        }

        private static void BuildCircleProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);
            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);

            XYZ center3D = UVToXYZ(cu, cv, planeDirection, unitType, planeOffsetFeet);
            XYZ uDir, vDir;
            switch (planeDirection)
            {
                case "x": uDir = XYZ.BasisY; vDir = XYZ.BasisZ; break;
                case "y": uDir = XYZ.BasisX; vDir = XYZ.BasisZ; break;
                default: uDir = XYZ.BasisX; vDir = XYZ.BasisY; break;
            }

            XYZ top = center3D.Add(vDir.Multiply(radiusFeet));
            XYZ bottom = center3D.Add(vDir.Multiply(-radiusFeet));
            XYZ right = center3D.Add(uDir.Multiply(radiusFeet));
            XYZ left = center3D.Add(uDir.Multiply(-radiusFeet));

            curves.Append(Arc.Create(top, bottom, right));
            curves.Append(Arc.Create(bottom, top, left));
        }

        private static void BuildCircleAsPolygonProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);

            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);
            const double minEdgeFeet = 0.01;
            int segments = 8;
            if (radiusFeet > 0)
            {
                double edgeLength = 2.0 * radiusFeet * Math.Sin(Math.PI / segments);
                while (edgeLength < minEdgeFeet && segments > 3)
                {
                    segments--;
                    edgeLength = 2.0 * radiusFeet * Math.Sin(Math.PI / segments);
                }
            }

            var points = new List<XYZ>();
            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * Math.PI * i / segments;
                double pu = cu + radius * Math.Cos(angle);
                double pv = cv + radius * Math.Sin(angle);
                points.Add(UVToXYZ(pu, pv, planeDirection, unitType, planeOffsetFeet));
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                curves.Append(Line.CreateBound(points[i], points[next]));
            }
        }

        private static void BuildCustomProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var vertices = profile.GetProperty("vertices");
            var points = new List<XYZ>();

            foreach (var vertex in vertices.EnumerateArray())
            {
                double u = EvaluateExpression(GetExpressionString(vertex.GetProperty("u")), defaults);
                double v = EvaluateExpression(GetExpressionString(vertex.GetProperty("v")), defaults);
                points.Add(UVToXYZ(u, v, planeDirection, unitType, planeOffsetFeet));
            }

            if (points.Count < 3) return;

            for (int i = 0; i < points.Count; i++)
            {
                int next = (i + 1) % points.Count;
                curves.Append(Line.CreateBound(points[i], points[next]));
            }
        }

        // ==================== SWEEP PROFILE BUILDERS ====================

        private static void BuildSweepRectangleProfile(
            JsonElement profile, Dictionary<string, double> defaults,
            ForgeTypeId unitType, XYZ pathStart,
            XYZ profileUAxis, XYZ profileVAxis, CurveArray curves)
        {
            var origin = profile.GetProperty("origin");
            double ou = EvaluateExpression(GetExpressionString(origin.GetProperty("u")), defaults);
            double ov = EvaluateExpression(GetExpressionString(origin.GetProperty("v")), defaults);
            double w = EvaluateExpression(GetExpressionString(profile.GetProperty("width")), defaults);
            double h = EvaluateExpression(GetExpressionString(profile.GetProperty("height")), defaults);

            double ouFeet = UnitUtils.ConvertToInternalUnits(ou, unitType);
            double ovFeet = UnitUtils.ConvertToInternalUnits(ov, unitType);
            double wFeet = UnitUtils.ConvertToInternalUnits(w, unitType);
            double hFeet = UnitUtils.ConvertToInternalUnits(h, unitType);

            XYZ p0 = pathStart.Add(profileUAxis.Multiply(ouFeet)).Add(profileVAxis.Multiply(ovFeet));
            XYZ p1 = p0.Add(profileUAxis.Multiply(wFeet));
            XYZ p2 = p1.Add(profileVAxis.Multiply(hFeet));
            XYZ p3 = p0.Add(profileVAxis.Multiply(hFeet));

            curves.Append(Line.CreateBound(p0, p1));
            curves.Append(Line.CreateBound(p1, p2));
            curves.Append(Line.CreateBound(p2, p3));
            curves.Append(Line.CreateBound(p3, p0));
        }

        private static void BuildSweepCircleProfile(
            JsonElement profile, Dictionary<string, double> defaults,
            ForgeTypeId unitType, XYZ pathStart,
            XYZ profileUAxis, XYZ profileVAxis, CurveArray curves)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);

            double cuFeet = UnitUtils.ConvertToInternalUnits(cu, unitType);
            double cvFeet = UnitUtils.ConvertToInternalUnits(cv, unitType);
            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);

            XYZ center3D = pathStart.Add(profileUAxis.Multiply(cuFeet)).Add(profileVAxis.Multiply(cvFeet));
            XYZ top = center3D.Add(profileVAxis.Multiply(radiusFeet));
            XYZ bottom = center3D.Add(profileVAxis.Multiply(-radiusFeet));
            XYZ right = center3D.Add(profileUAxis.Multiply(radiusFeet));
            XYZ left = center3D.Add(profileUAxis.Multiply(-radiusFeet));

            curves.Append(Arc.Create(top, bottom, right));
            curves.Append(Arc.Create(bottom, top, left));
        }
    }
}
