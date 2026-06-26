using System;
using System.Collections.Generic;
using System.Text;
using System.Web;

namespace HMI.Function
{
    internal class RecipeManager
    {
        private List<Models.RecipesModel> recipes = new List<Models.RecipesModel>();

        public void AddRecipe(string name, string description, List<string> tagNames, List<double> ingredients) //Aggiungi nuova ricetta
        {
            var recipe = new Models.RecipesModel
            {
                Name = name,
                Description = description,
                TagName = tagNames,
                Ingredients = ingredients
            };
            recipes.Add(recipe);
        }

        public void AddTagToRecipe(string recipeName, string tagName, double ingredient) //Aggiungi un tag a una ricetta esistente
        {
            var recipe = recipes.Find(r => r.Name == recipeName);
            if (recipe != null)
            {
                recipe.TagName.Add(tagName);
                recipe.Ingredients.Add(ingredient);
            }
        }

        public void ChangeRecipe(string name, string newDescription, List<string> newTagNames, List<double> newIngredients) //Modifica una ricetta esistente
        {
            var recipe = recipes.Find(r => r.Name == name);
            if (recipe != null)
            {
                recipe.Description = newDescription;
                recipe.TagName = newTagNames;
                recipe.Ingredients = newIngredients;
            }
        }

        public void ChangeRecipeName(string oldName, string newName) //Modifica il nome di una ricetta esistente
        {
            var recipe = recipes.Find(r => r.Name == oldName);
            if (recipe != null)
            {
                recipe.Name = newName;
            }
        }

        public void DeleteRecipe(string name) //Elimina una ricetta esistente
        {
            var recipe = recipes.Find(r => r.Name == name);
            if (recipe != null)
            {
                recipes.Remove(recipe);
            }
        }

        public void DeleteTag(string recipeName, string TagName) //Elimina il tag 
        {
            try
            {
                // 1. Trova la ricetta specifica
                var recipe = recipes.Find(r => r.Name == recipeName);

                if (recipe != null)
                {
                    // 2. Trova l'indice (la posizione) del tag nella lista
                    int index = recipe.TagName.IndexOf(TagName);

                    // Se IndexOf restituisce -1, significa che il tag non esiste. 
                    // Se è >= 0, procediamo con l'eliminazione.
                    if (index >= 0)
                    {
                        // 3. Elimina il tag
                        recipe.TagName.RemoveAt(index);

                        // 4. Elimina la quantità dell'ingrediente corrispondente.
                        // Aggiungiamo un controllo per sicurezza, nel caso le liste fossero disallineate.
                        if (index < recipe.Ingredients.Count)
                        {
                            recipe.Ingredients.RemoveAt(index);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting tag: {ex.Message}");
            }
        }

    }
}
