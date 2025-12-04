using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;




public class Recipe
{
    [Key]
    public int Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Category { get; set; }
    public string? FileId { get; set; } // Ссылка на файл в Телеграме
    public string MediaType { get; set; } = "Text"; // "Text", "Photo", "Video"
}


public class BotUser
{
    [Key]
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public string FirstName { get; set; }
    public DateTime FirstVisit { get; set; }
}

// Настройка базы данных
public class ApplicationContext : DbContext
{
    public DbSet<Recipe> Recipes { get; set; } = null!;
    public DbSet<BotUser> Users { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        
        optionsBuilder.UseSqlite("Data Source=bot3.db");
    }
}


public class UserSession
{
    public string Step { get; set; } = "None";
    public string Category { get; set; } = "";
    public string TempTitle { get; set; } = "";
    public int EditingRecipeId { get; set; } = 0;
}

internal class Program
{
    private static ITelegramBotClient botClient;

    
    private static long AdminId = 12345; 

    private static Dictionary<long, UserSession> _userSessions = new Dictionary<long, UserSession>();

    private static async Task Main(string[] args)
    {
        
        using (var db = new ApplicationContext()) { db.Database.EnsureCreated(); }
        botClient = new TelegramBotClient("API");
        botClient.StartReceiving(HandleUpdateAsync, HandlePollingErrorAsync, new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() });
        var me = await botClient.GetMeAsync();
        Console.WriteLine($"✅ Бот @{me.Username} запущен! Логи пишутся ниже...");
        Console.ReadLine();
    }

    private static Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken _)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
        return Task.CompletedTask;
    }

    async static Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken _)
    {
        using var db = new ApplicationContext();
        //логи
        long userId = 0;
        string username = "Unknown";
        string firstName = "Unknown";
        string actionType = "";

        if (update.Message != null)
        {
            userId = update.Message.Chat.Id;
            username = update.Message.Chat.Username ?? "NoNick";
            firstName = update.Message.Chat.FirstName ?? "NoName";
            actionType = $"Сообщение: {update.Message.Text ?? "[Медиа]"}";
        }
        else if (update.CallbackQuery != null)
        {
            userId = update.CallbackQuery.Message!.Chat.Id;
            username = update.CallbackQuery.From.Username ?? "NoNick";
            firstName = update.CallbackQuery.From.FirstName ?? "NoName";
            actionType = $"Кнопка: {update.CallbackQuery.Data}";
        }
        else return;

        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
        Console.ResetColor();
        Console.WriteLine($"{firstName} (@{username}): {actionType}");

        // сохраняет в базу, если новый
        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.ChatId == userId);
        if (existingUser == null)
        {
            db.Users.Add(new BotUser { ChatId = userId, Username = username, FirstName = firstName, FirstVisit = DateTime.Now });
            await db.SaveChangesAsync();
        }

        if (update.CallbackQuery is { } callback)
        {
            try { await client.AnswerCallbackQueryAsync(callback.Id); } catch { } // Защита от старых кнопок
            var chatId = userId;
            var data = callback.Data!;

            
            if (data == "oven" || data == "pan" || data == "no_cook")
            {
                await ShowRecipesList(client, db, chatId, data);
            }

            
            else if (data.StartsWith("show_"))
            {
                if (int.TryParse(data.Replace("show_", ""), out int recipeId))
                {
                    var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == recipeId);
                    if (recipe != null)
                    {
                        var buttons = new List<InlineKeyboardButton[]>();
                        // кнопки админа
                        if (chatId == AdminId)
                        {
                            buttons.Add(new[] {
                                InlineKeyboardButton.WithCallbackData("✏️ Редактировать", "edit_menu_" + recipe.Id),
                                InlineKeyboardButton.WithCallbackData("❌ Удалить", "del_" + recipe.Id)
                            });
                        }
                        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔙 Назад к списку", recipe.Category) });

                        var markup = new InlineKeyboardMarkup(buttons);

                        if (recipe.MediaType == "Photo")
                            await client.SendPhotoAsync(chatId, InputFile.FromFileId(recipe.FileId!), caption: recipe.Description, replyMarkup: markup);
                        else if (recipe.MediaType == "Video")
                            await client.SendVideoAsync(chatId, InputFile.FromFileId(recipe.FileId!), caption: recipe.Description, replyMarkup: markup);
                        else
                            await client.SendTextMessageAsync(chatId, $"<b>🍽 {recipe.Title}</b>\n\n{recipe.Description}", parseMode: ParseMode.Html, replyMarkup: markup);
                    }
                }
            }

           
            else if (data.StartsWith("edit_menu_"))
            {
                if (chatId != AdminId) return;
                var id = int.Parse(data.Replace("edit_menu_", ""));
                var editButtons = new InlineKeyboardMarkup(new[] {
                    new [] { InlineKeyboardButton.WithCallbackData("📝 Изм. Название", "edit_title_" + id) },
                    new [] { InlineKeyboardButton.WithCallbackData("📄 Изм. Описание", "edit_desc_" + id) },
                    new [] { InlineKeyboardButton.WithCallbackData("🖼 Изм. Фото/Видео", "edit_media_" + id) },
                    new [] { InlineKeyboardButton.WithCallbackData("🔙 Отмена", "show_" + id) }
                });
                await client.SendTextMessageAsync(chatId, "Что хотите изменить?", replyMarkup: editButtons);
            }
            
            else if (data.StartsWith("edit_title_")) { _userSessions[chatId] = new UserSession { Step = "EditWaitTitle", EditingRecipeId = int.Parse(data.Replace("edit_title_", "")) }; await client.SendTextMessageAsync(chatId, "Введите новое название:"); }
            else if (data.StartsWith("edit_desc_")) { _userSessions[chatId] = new UserSession { Step = "EditWaitDesc", EditingRecipeId = int.Parse(data.Replace("edit_desc_", "")) }; await client.SendTextMessageAsync(chatId, "Введите новое описание:"); }
            else if (data.StartsWith("edit_media_")) { _userSessions[chatId] = new UserSession { Step = "EditWaitMedia", EditingRecipeId = int.Parse(data.Replace("edit_media_", "")) }; await client.SendTextMessageAsync(chatId, "Пришлите новое фото или видео:"); }

            
            else if (data.StartsWith("del_"))
            {
                if (chatId != AdminId) return;
                if (int.TryParse(data.Replace("del_", ""), out int idToDelete))
                {
                    var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == idToDelete);
                    if (recipe != null) { var cat = recipe.Category; db.Recipes.Remove(recipe); await db.SaveChangesAsync(); await client.SendTextMessageAsync(chatId, "✅ Удалено."); await ShowRecipesList(client, db, chatId, cat); }
                }
            }

            
            else if (data.StartsWith("add_cat_"))
            {
                if (chatId != AdminId) return;
                var cat = data.Replace("add_cat_", "");
                _userSessions[chatId] = new UserSession { Step = "WaitTitle", Category = cat };
                await client.SendTextMessageAsync(chatId, $"Категория: {cat}. Введите Название:");
            }
            
            else if (data == "admin_add")
            {
                if (chatId != AdminId) return;
                var buttons = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("На сковороде", "add_cat_pan"), InlineKeyboardButton.WithCallbackData("Духовка", "add_cat_oven") }, new[] { InlineKeyboardButton.WithCallbackData("Без готовки", "add_cat_no_cook") } });
                await client.SendTextMessageAsync(chatId, "Куда добавляем?", replyMarkup: buttons);
            }
            // главное меню
            else if (data == "main_menu") await SendMainMenu(client, chatId);

            return;
        }

        //фото, видео
        if (update.Message is not { } message) return;
        var msgChatId = message.Chat.Id;

        
        if (_userSessions.ContainsKey(msgChatId))
        {
            var session = _userSessions[msgChatId];

            
            if (session.Step.StartsWith("Edit"))
            {
                var recipeToEdit = await db.Recipes.FirstOrDefaultAsync(r => r.Id == session.EditingRecipeId);
                if (recipeToEdit == null) { await client.SendTextMessageAsync(msgChatId, "Ошибка: Рецепт не найден."); _userSessions.Remove(msgChatId); return; }

                if (session.Step == "EditWaitTitle")
                {
                    recipeToEdit.Title = message.Text ?? "";
                    await client.SendTextMessageAsync(msgChatId, "✅ Название обновлено.");
                }
                else if (session.Step == "EditWaitDesc")
                {
                    recipeToEdit.Description = message.Text ?? "";
                    await client.SendTextMessageAsync(msgChatId, "✅ Описание обновлено.");
                }
                else if (session.Step == "EditWaitMedia")
                {
                    if (message.Photo != null) { recipeToEdit.MediaType = "Photo"; recipeToEdit.FileId = message.Photo.Last().FileId; if (message.Caption != null) recipeToEdit.Description = message.Caption; }
                    else if (message.Video != null) { recipeToEdit.MediaType = "Video"; recipeToEdit.FileId = message.Video.FileId; if (message.Caption != null) recipeToEdit.Description = message.Caption; }
                    else { await client.SendTextMessageAsync(msgChatId, "Нужно фото или видео."); return; }
                    await client.SendTextMessageAsync(msgChatId, "✅ Медиа обновлено.");
                }

                await db.SaveChangesAsync();
                _userSessions.Remove(msgChatId);

                
                await SendMainMenu(client, msgChatId);
                return;
            }

            
            if (session.Step == "WaitTitle")
            {
                if (string.IsNullOrEmpty(message.Text)) { await client.SendTextMessageAsync(msgChatId, "Пришлите название текстом."); return; }
                session.TempTitle = message.Text;
                session.Step = "WaitContent";
                await client.SendTextMessageAsync(msgChatId, "Теперь пришлите Рецепт (Текст, Фото или Видео).");
                return;
            }
            else if (session.Step == "WaitContent")
            {
                var newRecipe = new Recipe { Category = session.Category, Title = session.TempTitle };
                if (message.Photo != null) { newRecipe.MediaType = "Photo"; newRecipe.FileId = message.Photo.Last().FileId; newRecipe.Description = message.Caption ?? ""; }
                else if (message.Video != null) { newRecipe.MediaType = "Video"; newRecipe.FileId = message.Video.FileId; newRecipe.Description = message.Caption ?? ""; }
                else if (message.Text != null) { newRecipe.MediaType = "Text"; newRecipe.Description = message.Text; }
                else return;

                db.Recipes.Add(newRecipe);
                await db.SaveChangesAsync();
                _userSessions.Remove(msgChatId);
                await client.SendTextMessageAsync(msgChatId, "✅ Рецепт сохранен!");
                await ShowRecipesList(client, db, msgChatId, newRecipe.Category);
                return;
            }
        }

        
        var text = message.Text?.ToLower();

        // показать пользователей
        if (text == "/users" && msgChatId == AdminId)
        {
            var users = await db.Users.ToListAsync();
            var response = $"📊 <b>Пользователи: {users.Count}</b>\n\n";
            foreach (var u in users) response += $"👤 {u.FirstName} (@{u.Username}) — {u.FirstVisit:dd.MM.yy}\n";
            await client.SendTextMessageAsync(msgChatId, response, parseMode: ParseMode.Html);
            return;
        }

        if (text == "/add")
        {
            if (msgChatId != AdminId) return;
            var buttons = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("На сковороде", "add_cat_pan"), InlineKeyboardButton.WithCallbackData("Духовка", "add_cat_oven") }, new[] { InlineKeyboardButton.WithCallbackData("Без готовки", "add_cat_no_cook") } });
            await client.SendTextMessageAsync(msgChatId, "Куда добавляем?", replyMarkup: buttons);
            return;
        }

        if (text != null && (text.Contains("/menu") || text.Contains("привет")))
        {
            await SendMainMenu(client, msgChatId);
            return;
        }

        await client.SendTextMessageAsync(msgChatId, "Я вас не понимаю 🤷‍♂️\nНажмите /menu");
    }

    
    private static async Task ShowRecipesList(ITelegramBotClient client, ApplicationContext db, long chatId, string category)
    {
        var recipes = await db.Recipes.Where(r => r.Category == category).ToListAsync();
        var buttonsList = new List<InlineKeyboardButton[]>();
        foreach (var recipe in recipes) buttonsList.Add(new[] { InlineKeyboardButton.WithCallbackData(recipe.Title, "show_" + recipe.Id) });
        buttonsList.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ В главное меню", "main_menu") });
        string catName = category == "oven" ? "Духовка" : (category == "pan" ? "Сковорода" : "Без готовки");
        await client.SendTextMessageAsync(chatId, $"📂 {catName}", replyMarkup: new InlineKeyboardMarkup(buttonsList));
    }

    private static async Task SendMainMenu(ITelegramBotClient client, long chatId)
    {
        var rows = new List<InlineKeyboardButton[]> {
            new [] { InlineKeyboardButton.WithCallbackData("🍳 На сковороде", "pan"), InlineKeyboardButton.WithCallbackData("🧖‍♀️ Духовка", "oven") },
            new [] { InlineKeyboardButton.WithCallbackData("🥗 Не нужна готовка", "no_cook") }
        };
        // Кнопка добавления только для админа
        if (chatId == AdminId) rows.Add(new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить рецепт", "admin_add") });

        await client.SendPhotoAsync(
            chatId: chatId,
            photo: InputFile.FromUri("https://rms4.kufar.by/v1/gallery/adim1/9556c7ec-a70a-4ffb-bef8-4fae71d15a0f.jpg"),
            caption: "ПП рецепты на каждый день. Выбери категорию:",
            replyMarkup: new InlineKeyboardMarkup(rows)
        );
    }
}