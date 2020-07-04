﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[System.Serializable]
public class ScaleStage
{



    // ____________________________________________________________________________________________________
    // Public

    [Header("Settings and Balancing")]
    public  Evolution_Settings     evolution_settings;                    // This holds all the data regarding population pool, genums and mutation
    public  Fitness_Settings       fitness_settings;                      // all the balancing paramters of the fitness function
    public  Scale_Settings         scale_settings;                        // all the balancing parameters regarding the creation of this scale such as the gaussian and sobel filter balancing stuff


    // ____________________________________________________________________________________________________
    // Private

    private Texture                ImageToReproduce;                      // This image is used for the evolution algo and is the ground truth. Passed on from outside
    private Compute_Shaders        compute_shaders;                       // Reference to the compute shaders, This object is not constructed within the stage and is only passed on as a reference to each scale stage
    private Compute_Resources      compute_resources;                     // holds all the buffers and rendertargets. Look in the defination of the struct for more info and documentation
    private CommandBuffer          stage_command_buffer;                  // this command buffer encapsulates everything that happens in the effect

    private Material               rendering_material;                    // material used to actually render the brush strokes
    private Material               fittest_rendering_material;            // this material is used to draw the fittest member of the population at the end of the lop after all calculations are done
           
    private uint                   scale_stage_id;                        // This is the identifier for this scale stage. 
    
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    // Constructor
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    public void initialise_stage(Texture original_image, RenderTexture clear_with_base, Compute_Shaders shaders, bool is_black_white, uint stage_id)
    {
        // ____________________________________________________________________________________________________
        // Initializing Parameter passed from Evolution Manager

        ImageToReproduce = original_image;
        compute_shaders  = shaders;
        scale_stage_id   = stage_id;

        // ____________________________________________________________________________________________________
        // Compute Resources Initialization
        compute_resources = new Compute_Resources();
        compute_resources.Consruct_Buffers(ImageToReproduce, clear_with_base, evolution_settings);

        // ____________________________________________________________________________________________________
        // Materials

        rendering_material = new Material(Shader.Find("Unlit/PopulationShader"));
        if (!rendering_material) Debug.LogError("Couldnt find the population shader");

        rendering_material.SetTexture("_MainTex", evolution_settings.brushTexture);

        fittest_rendering_material = new Material(Shader.Find("Unlit/FittestRenderShader"));
        if (!fittest_rendering_material) Debug.LogError("could not find the sahder for rendering the fittest population member");

        fittest_rendering_material.SetInt("_genes_number_per_member", (int)evolution_settings.maximumNumberOfBrushStrokes);
        fittest_rendering_material.SetTexture("_MainTex", evolution_settings.brushTexture);

        rendering_material.SetBuffer         ("_population_pool",         compute_resources.population_pool_buffer);
        rendering_material.SetBuffer         ("_population_pool",         compute_resources.population_pool_buffer);
        fittest_rendering_material.SetBuffer ("_population_pool",         compute_resources.population_pool_buffer);
        fittest_rendering_material.SetBuffer ("_fittest_member" ,         compute_resources.fittest_member_buffer);
        // ____________________________________________________________________________________________________
        // Compute Shader Bindings

        compute_shaders.Bind_Compute_Resources(ImageToReproduce, compute_resources, evolution_settings);

        // ____________________________________________________________________________________________________
        // CPU Arrays initalization

        // -----------------------
        // Population Pool first gen initializatiopn

        int total_number_of_genes = (int)(evolution_settings.populationPoolNumber * evolution_settings.maximumNumberOfBrushStrokes);
        Genes[] initialPop = new Genes[total_number_of_genes];

        if(!is_black_white)
        CPUSystems.InitatePopulationMember(ref initialPop, evolution_settings.brushSizeLowerBound, 
            evolution_settings.brushSizeHigherBound);
        else
        CPUSystems.InitatePopulationMemberBW(ref initialPop, evolution_settings.brushSizeLowerBound,
            evolution_settings.brushSizeHigherBound);

        // -----------------------
        // Sending the Data to the GPU
        compute_resources.population_pool_buffer.SetData(initialPop);

        // ____________________________________________________________________________________________________
        // Command Buffer Generation

        stage_command_buffer = new CommandBuffer
        {
            name = string.Format("Stage_{0}_Command_Buffer", scale_stage_id.ToString()),
        };

        ClearAllRenderTargets(ref stage_command_buffer, true, true, Color.white);

        if (ImageToReproduce.width % 32 != 0 || ImageToReproduce.height % 32 != 0)
            Debug.LogError("image is not multiply of 32. Either change the image dimensions or" +
             "The threadnumbers set up in the compute shaders!");

        // -----------------------
        // Command Buffer Recording

        uint shader_population_member_begining_index = 0;

        for (int i = 0; i < evolution_settings.populationPoolNumber; i++)
        {
            stage_command_buffer.SetGlobalInt("_memember_begin_stride", (int)shader_population_member_begining_index);                         // This is used in the PopulationShader to sample the currect population member (brush stroke) from the population pool list. It is baisicly population_member_index * genes_number_per_population
            shader_population_member_begining_index += evolution_settings.maximumNumberOfBrushStrokes;                                         // add the genes number (stride) in every loop iteration instead of caculating  population_member_index * genes_number_per_population every time

            // -----------------------
            // Draw Population Pool Member

            ClearAllRenderTargets(ref stage_command_buffer, true, true, Color.white);
            stage_command_buffer.DrawProcedural(Matrix4x4.identity, rendering_material, 0, 
                MeshTopology.Triangles, (int)evolution_settings.maximumNumberOfBrushStrokes * 6);

            // -----------------------
            //Compute Fitness
            stage_command_buffer.CopyTexture(compute_resources.active_texture_target, compute_resources.compute_forged_in_render_texture);     // Without copying the rendering results to a new buffer, I was getting weird results after the rendering of the first population member. Seemed like unity unbinds this buffer since it thinks another operation is writing to it and binds a compeletly different buffer as input (auto generated one). The problem is gone if you copy the buffer 
            stage_command_buffer.SetGlobalInt("_population_id_handel", i);                                                                     // the id is used in compute to know which of the populatioin members are currently being dealt with. 

            // thread groups are made up 32 in 32 threads. The image should be a multiply of 32. 
            // so ideally padded to a power of 2. For other image dimensions, the threadnums
            // should be changed in the compute shader for the kernels as well as here the 
            // height or width divided by 32. Change it only if you know what you are doing 
            stage_command_buffer.DispatchCompute(compute_shaders.compute_fitness_function, compute_shaders.per_pixel_fitness_kernel_handel,    // Compute the fittness of each individual pixel 
                ImageToReproduce.width / 32, ImageToReproduce.height / 32, 1);

            // dispatch one compute per row in groups of 64
            stage_command_buffer.DispatchCompute(compute_shaders.compute_fitness_function, compute_shaders.sun_rows_kernel_handel,             // Sum up each row to a single value. So a new array will come out of this which is a single column that has as many member as the height of the image
                ImageToReproduce.height / 32, 1, 1);

            //// dispatch a single thread
            stage_command_buffer.DispatchCompute(compute_shaders.compute_fitness_function, compute_shaders.sun_column_kernel_handel,
                1, 1, 1);
        }

        // -----------------------
        // Convert Fitness to accumlative weighted probablities

        // Dispatch single thread. 
        stage_command_buffer.DispatchCompute(compute_shaders.compute_selection_functions,                                                      // dispatching only a single thread is a waste of a wave front and generally gpu resrources. There  are better reduction algorithmns designed for GPU, have a look at those
            compute_shaders.trans_fitness_to_prob_handel, 1, 1, 1);

        // -----------------------
        // Redraw The Fittest of the Population Members
        ClearAllRenderTargets(ref stage_command_buffer, true, true, Color.white);

        stage_command_buffer.DrawProcedural(Matrix4x4.identity, fittest_rendering_material, 0,
            MeshTopology.Triangles, (int)evolution_settings.maximumNumberOfBrushStrokes * 6);
        stage_command_buffer.Blit(compute_resources.active_texture_target, BuiltinRenderTextureType.CameraTarget);

        if (evolution_settings.populationPoolNumber % 16 != 0)
            Debug.LogError("The population pool number is set to" + evolution_settings.populationPoolNumber +
             "Which is not multiple of 32. Either change this number or numThreads in the compute shader!");

        // -----------------------
        // Select parents for each member of the second generation pool based on the fitness of each member

        stage_command_buffer.DispatchCompute(compute_shaders.compute_selection_functions,
            compute_shaders.parent_selection_handel, (int)evolution_settings.populationPoolNumber / 16, 1, 1);

        if (total_number_of_genes % 128 != 0)
            Debug.LogError(string.Format("Total number of genes in the population pool is: {0}, which is not a factor of 128. " +
                "Either change this number to a factor of 128 or change the numThreads in the compute shader"));

        // -----------------------
        // Create each member of the second generation by combining the genes of the parents

        stage_command_buffer.DispatchCompute(compute_shaders.compute_selection_functions, 
            compute_shaders.cross_over_handel, total_number_of_genes / 128, 1, 1);

        if (!is_black_white)
            stage_command_buffer.DispatchCompute(compute_shaders.compute_selection_functions,                                                  // This copies the cross overed genes from second gen to the main buffer for rendering in next frame and also mutates some of them
                compute_shaders.mutation_and_copy_handel, total_number_of_genes / 128, 1, 1);     
        else
            stage_command_buffer.DispatchCompute(compute_shaders.compute_selection_functions,                                                  // This copies the cross overed genes from second gen to the main buffer for rendering in next frame and also mutates some of them. However mutation is in black and white
                compute_shaders.mutation_and_copy_BW_handel, total_number_of_genes / 128, 1, 1);

        Camera.main.AddCommandBuffer(CameraEvent.AfterEverything, stage_command_buffer);                                                       // Evolution Master has already checked that the Camera main is valid. Dispatch the Command buffer after everything

    }


    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    // DESTRUCTOR
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------


    public void deinitialize_stage(ref RenderTexture copy_result_in_to)
    {
        Graphics.Blit(compute_resources.active_texture_target, copy_result_in_to);
        compute_resources.Destruct_Buffers();                                                                                                   // Call the destructor on the GPU buffers. This causes a reference decremeanting on the COM objects
        Camera.main.RemoveCommandBuffer(CameraEvent.AfterEverything, stage_command_buffer);                                                     // Removing the Command buffer from the camera so that this rendering doesnt happen anymore.
    }

    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    // UPTADE
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------


    public void update_stage(uint generation_id)
    {
        UpdateBalancingParameters(generation_id);

        MemberIDFitnessPair[] fittestMember = new MemberIDFitnessPair[1];

        compute_resources.fittest_member_buffer.GetData(fittestMember);

        // -----------------------
        // Print out statistics

        Debug.Log(string.Format("Current total Generation {0}, Current Stage , {1}, current fittest member is: {2} with a fittness value of {3}",
            generation_id, scale_stage_id, fittestMember[0].memberID, fittestMember[0].memberFitness));
    }


    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    // HELPER_FUNCTIONS
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------


    private void UpdateBalancingParameters(uint generation_id)
    {
        
        compute_shaders.compute_selection_functions.SetInt  ("_generation_seed",(int)generation_id + Random.Range(0, 2147483647));                                                       // This number is used in the compute shader to differention between rand number geneartion between different generations
        compute_shaders.compute_selection_functions.SetFloat("_scale_lower_bound",   evolution_settings.brushSizeLowerBound);
        compute_shaders.compute_selection_functions.SetFloat("_scale_higher_bound",  evolution_settings.brushSizeHigherBound);
        compute_shaders.compute_selection_functions.SetFloat("_mutation_rate",       evolution_settings.mutationChance);
        compute_shaders.compute_selection_functions.SetFloat("_fittness_pow_factor", fitness_settings.fitnessPowFactor);



        compute_shaders.compute_fitness_function.SetFloat("_hue_weight", fitness_settings.hueWeight);
        compute_shaders.compute_fitness_function.SetFloat("_sat_weight", fitness_settings.satWeight);
        compute_shaders.compute_fitness_function.SetFloat("_val_weight", fitness_settings.valWeight);
    }

    private void ClearAllRenderTargets(ref CommandBuffer cb, bool color, bool depth, Color c)
    {

        //if this is called after a compute. it might cause sync issues with the compute. never understood how the 
        //the barriers and syncing is done in unity. So keep an eye out

        cb.SetRenderTarget(compute_resources.compute_forged_in_render_texture);         // The texture which is used as input for the compute shader also needs to be cleared
        cb.ClearRenderTarget(color, depth, c);


        cb.SetRenderTarget(compute_resources.active_texture_target);                    // make sure you end up with active_texture_target being the last bound render target
        cb.Blit(compute_resources.clear_base, compute_resources.active_texture_target);
    }



    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    // DEBUG
    // ----------------------------------------------------------------------------------------------------------------------------------------------------------------
    
    
    struct parentPair
    {
        public int parentX, parentY;
    }


    void debug_cross_over()
    {
        Genes[] second_gen = new Genes[evolution_settings.maximumNumberOfBrushStrokes * evolution_settings.populationPoolNumber];
        compute_resources.second_gen_population_pool_buffer.GetData(second_gen);

        population_member_to_string(second_gen);
    }

    void debug_parent_selection()
    {
        parentPair[] second_gen_parents_ids = new parentPair[evolution_settings.populationPoolNumber];
        compute_resources.second_gen_parents_ids_buffer.GetData(second_gen_parents_ids);
        for (int i = 0; i < evolution_settings.populationPoolNumber; i++)
        {
            Debug.Log(string.Format("second generation {0}, has the parents {1} and {2}", 
                i, second_gen_parents_ids[i].parentX, second_gen_parents_ids[i].parentY));
        }
    }


    void debug_fitness_to_probabilities_transformation()
    {
        float[] accmulative_probabilities = new float[evolution_settings.populationPoolNumber];
        compute_resources.population_accumlative_prob_buffer.GetData(accmulative_probabilities);
        for (int i = 0; i < evolution_settings.populationPoolNumber; i++)
        {
            Debug.Log(string.Format("population {0}, has the accumalative weighted probablity {1}", i, accmulative_probabilities[i]));
        }

        uint[] fittestMember = new uint[1];
        compute_resources.fittest_member_buffer.GetData(fittestMember);
        Debug.Log(string.Format("The fittest population member is the member {0}", fittestMember[0]));
    }

    /// <summary>
    /// Pulls the fitness value data from the GPU and prints them out per member
    /// </summary>
    void debug_population_member_fitness_value()
    {
        float[] population_fitness_cpu_values = new float[evolution_settings.populationPoolNumber];
        compute_resources.population_pool_fitness_buffer.GetData(population_fitness_cpu_values);

        for (int i = 0; i < evolution_settings.populationPoolNumber; i++)
        {
            Debug.Log(string.Format("population {0}, has fitnesss {1}", i, population_fitness_cpu_values[i]));
        }
    }

    void texture_to_RenderTexture(Texture toConvert, RenderTexture toBlitTo)
    {
        RenderTexture temp_ref = RenderTexture.active;
        RenderTexture.active   = toBlitTo;

        Graphics.Blit(toConvert, toBlitTo);
        RenderTexture.active   = temp_ref;
    }

     void population_member_to_string(Genes[] arrayToString)
    {
        for(int i =0; i< arrayToString.Length; i++)
        {
            Debug.Log( "gene [" + i+ "] : "+ GenesToString(arrayToString[i]));
        }
    }

        string GenesToString(Genes g)
    {
        return string.Format("position: ({0}, {1}), rotation: {2}, scale: ({3}, {4}), color: ({5}, {6}, {7}), textureID: {8}",
                              g.position_X, g.position_Y, g.z_Rotation, g.scale_X, g.scale_Y, 
                              g.color_r, g.color_g, g.color_b, g.texture_ID);
    }

}
